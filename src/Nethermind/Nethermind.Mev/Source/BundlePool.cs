//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Mev.Data;
using Nethermind.Mev.Execution;
using Nethermind.TxPool;
using Nethermind.TxPool.Collections;

namespace Nethermind.Mev.Source
{

    public class BundlePool : IBundlePool, ISimulatedBundleSource, IDisposable
    {
        private readonly ITimestamper _timestamper;
        private readonly ITxValidator _txValidator;
        private readonly IMevConfig _mevConfig;
        private readonly ISpecProvider _specProvider;
        private readonly IBlockTree _blockTree;
        private readonly IBundleSimulator _simulator;
        private readonly BundleSortedPool _bundles;
        private readonly ConcurrentDictionary<Address, MevBundle> _megabundles = new();
        private readonly ConcurrentDictionary<long, ConcurrentDictionary<(MevBundle Bundle, Keccak BlockHash), SimulatedMevBundleContext>> _simulatedBundles = new();
        private readonly ILogger _logger;
        private readonly IEthereumEcdsa _ecdsa;
        
        private long HeadNumber => _blockTree.Head?.Number ?? 0;
        
        public event EventHandler<BundleEventArgs>? NewReceivedBundle;
        public event EventHandler<BundleEventArgs>? NewPendingBundle;
        public event EventHandler<MegabundleEventArgs>? NewReceivedMegabundle;
        public event EventHandler<MegabundleEventArgs>? NewPendingMegabundle;
        
        public BundlePool(
            IBlockTree blockTree, 
            IBundleSimulator simulator,
            ITimestamper timestamper,
            ITxValidator txValidator, 
            ISpecProvider specProvider,
            IMevConfig mevConfig,
            ILogManager logManager,
            IEthereumEcdsa ecdsa)
        {
            _timestamper = timestamper;
            _txValidator = txValidator;
            _mevConfig = mevConfig;
            _specProvider = specProvider;
            _blockTree = blockTree;
            _simulator = simulator;
            _logger = logManager.GetClassLogger();
            _ecdsa = ecdsa;
            
            IComparer<MevBundle> comparer = CompareMevBundleByBlock.Default.ThenBy(CompareMevBundleByMinTimestamp.Default);
            _bundles = new BundleSortedPool(
                _mevConfig.BundlePoolSize,
                comparer,
                logManager );

            _bundles.Removed += OnBundleRemoved;
            _blockTree.NewHeadBlock += OnNewBlock;
            NewPendingMegabundle += OnNewPendingMegabundle;
        }

        public Task<IEnumerable<MevBundle>> GetBundles(BlockHeader parent, UInt256 timestamp, long gasLimit, CancellationToken token = default) => 
            Task.FromResult(GetBundles(parent.Number + 1, timestamp, token));

        public IEnumerable<MevBundle> GetBundles(long blockNumber, UInt256 timestamp, CancellationToken token = default) => 
            GetBundles(blockNumber, timestamp, timestamp, token);

        private IEnumerable<MevBundle> GetBundles(long blockNumber, UInt256 minTimestamp, UInt256 maxTimestamp, CancellationToken token = default)
        {
            if (_bundles.TryGetBucket(blockNumber, out MevBundle[] bundles))
            {
                foreach (MevBundle mevBundle in bundles)
                {
                    if (token.IsCancellationRequested)
                    {
                        break;
                    }

                    bool bundleIsInFuture = mevBundle.MinTimestamp != UInt256.Zero && minTimestamp < mevBundle.MinTimestamp;
                    bool bundleIsTooOld = mevBundle.MaxTimestamp != UInt256.Zero && maxTimestamp > mevBundle.MaxTimestamp;
                    if (!bundleIsInFuture && !bundleIsTooOld) 
                    {
                        yield return mevBundle;
                    }
                }
            }
        }

        public Task<MevBundle?> GetMegabundle(BlockHeader parent, UInt256 timestamp, long gasLimit,
            Address relayAddress, CancellationToken token = default) =>
            Task.FromResult(GetMegabundle(parent.Number + 1, timestamp, relayAddress, token));
        
        public MevBundle? GetMegabundle(long blockNumber, UInt256 timestamp, Address relayAddress, CancellationToken token = default) =>
            GetMegabundle(blockNumber, timestamp, timestamp, relayAddress, token);
        
        private MevBundle? GetMegabundle(long blockNumber, UInt256 minTimestamp, UInt256 maxTimestamp, Address relayAddress,
            CancellationToken token = default)
        {
            if (_megabundles.TryGetValue(relayAddress, out MevBundle? bundle))
            {
                if (token.IsCancellationRequested)
                {
                    return null;
                }
                
                bool bundleIsInFuture = bundle.MinTimestamp != UInt256.Zero && minTimestamp < bundle.MinTimestamp;
                bool bundleIsTooOld = bundle.MaxTimestamp != UInt256.Zero && maxTimestamp > bundle.MaxTimestamp;
                bool bundleIsInCurrentBlock = bundle.BlockNumber == blockNumber;
                if (!bundleIsInFuture && !bundleIsTooOld && bundleIsInCurrentBlock) 
                {
                    return bundle;
                }
            }

            return null;
        }

        public bool AddBundle(MevBundle bundle)
        {
            Metrics.BundlesReceived++;
            BundleEventArgs bundleEventArgs = new(bundle);
            NewReceivedBundle?.Invoke(this, bundleEventArgs);
            
            if (ValidateBundle(bundle))
            {
                bool result = _bundles.TryInsert(bundle, bundle);

                if (result)
                {
                    Metrics.ValidBundlesReceived++;
                    NewPendingBundle?.Invoke(this, bundleEventArgs);
                    if (bundle.BlockNumber == HeadNumber + 1)
                    { 
                        TrySimulateBundle(bundle);
                    }
                }

                return result;
            }

            return false;
        }

        public bool AddMegabundle(MevMegabundle megabundle)
        {
            Metrics.MegabundlesReceived++;
            Address relayAddress = _ecdsa.RecoverAddress(megabundle.RelaySignature, megabundle.Bundle.Hash)!;
            MegabundleEventArgs megabundleEventArgs = new(megabundle.Bundle, relayAddress);
            NewReceivedMegabundle?.Invoke(this, megabundleEventArgs);

            if (ValidateBundle(megabundle.Bundle) && IsTrustedRelay(relayAddress))
            {
                _megabundles.AddOrUpdate(relayAddress, 
                    _ => megabundle.Bundle, 
                    (_, _) => megabundle.Bundle);
                Metrics.ValidMegabundlesReceived++;
                NewPendingMegabundle?.Invoke(this, megabundleEventArgs);
                
                if (megabundle.Bundle.BlockNumber == HeadNumber + 1)
                { 
                    TrySimulateBundle(megabundle.Bundle);
                }

                return true;
            }

            return false;
        }
        
        private bool IsTrustedRelay(Address relayAddress)
        {
            return _mevConfig.GetTrustedRelayAddresses().Contains(relayAddress);
        }

        private bool ValidateBundle(MevBundle bundle)
        {
            if (HeadNumber >= bundle.BlockNumber)
            {
                return false;
            }

            if (bundle.Transactions.Count == 0)
            {
                if (_logger.IsDebug) _logger.Debug($"Bundle rejected, because it doesn't contain transactions.");
                return false;
            }

            ulong currentTimestamp = _timestamper.UnixTime.Seconds;

            if (bundle.MaxTimestamp < bundle.MinTimestamp)
            {
                if (_logger.IsDebug)
                    _logger.Debug(
                        $"Bundle rejected, because {nameof(bundle.MaxTimestamp)} {bundle.MaxTimestamp} is < {nameof(bundle.MinTimestamp)} {bundle.MinTimestamp}.");
                return false;
            }
            else if (bundle.MaxTimestamp != 0 && bundle.MaxTimestamp < currentTimestamp)
            {
                if (_logger.IsDebug)
                    _logger.Debug(
                        $"Bundle rejected, because {nameof(bundle.MaxTimestamp)} {bundle.MaxTimestamp} is < current {currentTimestamp}.");
                return false;
            }
            else if (bundle.MinTimestamp != 0 && bundle.MinTimestamp > currentTimestamp + _mevConfig.BundleHorizon)
            {
                if (_logger.IsDebug)
                    _logger.Debug(
                        $"Bundle rejected, because {nameof(bundle.MinTimestamp)} {bundle.MaxTimestamp} is further into the future than accepted horizon {_mevConfig.BundleHorizon}.");
                return false;
            }

            IReleaseSpec spec = _specProvider.GetSpec(bundle.BlockNumber);
            for (int i = 0; i < bundle.Transactions.Count; i++)
            {
                if (!_txValidator.IsWellFormed(bundle.Transactions[i], spec))
                {
                    return false;
                }
            }

            return true;
        }

        private bool TrySimulateBundle(MevBundle bundle)
        {
            var head = _blockTree.Head;
            if (head is not null)
            {
                if (head.Number + 1 == bundle.BlockNumber)
                {
                    SimulateBundle(bundle, head.Header);
                    return true;
                }
            }

            return false;
        }
              
        protected virtual SimulatedMevBundleContext? SimulateBundle(MevBundle bundle, BlockHeader parent)
        {
            SimulatedMevBundleContext? context = null;
            (MevBundle, Keccak) key = (bundle, parent.Hash!);

            Metrics.BundlesSimulated++;
            
            SimulatedMevBundleContext CreateContext()
            {
                CancellationTokenSource cancellationTokenSource = new();
                Task<SimulatedMevBundle> simulateTask = _simulator.Simulate(bundle, parent, cancellationTokenSource.Token);
                simulateTask.ContinueWith(TryRemoveFailedSimulatedBundle, cancellationTokenSource.Token);
                return context = new(simulateTask, cancellationTokenSource);
            }
            
            ConcurrentDictionary<(MevBundle, Keccak), SimulatedMevBundleContext> AddContext(ConcurrentDictionary<(MevBundle, Keccak), SimulatedMevBundleContext> d)
            {
                d.AddOrUpdate(key, 
                    _ => CreateContext(), 
                    (_, c) => c);
                return d;
            }
            
            _simulatedBundles.AddOrUpdate(parent.Number, 
                    _ =>
                    {
                        ConcurrentDictionary<(MevBundle, Keccak), SimulatedMevBundleContext> d = new();
                        return AddContext(d);
                    },
                    (_, d) => AddContext(d));

            return context;
        }

        private void TryRemoveFailedSimulatedBundle(Task<SimulatedMevBundle> simulateTask)
        {
            if (simulateTask.IsCompletedSuccessfully)
            {
                SimulatedMevBundle simulatedMevBundle = simulateTask.Result;
                if (!simulatedMevBundle.Success)
                {
                    _bundles.TryRemove(simulatedMevBundle.Bundle);
                    RemoveSimulation(simulatedMevBundle.Bundle);
                }
            }
        }

        private void OnNewBlock(object? sender, BlockEventArgs e)
        {
            long blockNumber = e.Block!.Number;
            RemoveBundlesUpToBlock(blockNumber);

            Task.Run(() =>
            {
                UInt256 timestamp = _timestamper.UnixTime.Seconds;
                IEnumerable<MevBundle> bundles = GetBundles(e.Block.Number + 1, UInt256.MaxValue, timestamp);
                foreach (MevBundle bundle in bundles)
                {
                    SimulateBundle(bundle, e.Block.Header);
                }
            });
        }

        private void RemoveBundlesUpToBlock(long blockNumber)
        {
            void StopSimulations(IEnumerable<SimulatedMevBundleContext> simulations)
            {
                foreach (SimulatedMevBundleContext simulation in simulations)
                {
                    StopSimulation(simulation);
                }
            }
            
            IDictionary<long, MevBundle[]> bundlesToRemove = _bundles.GetBucketSnapshot(b => b <= blockNumber);

            foreach (KeyValuePair<long, MevBundle[]> bundleBucket in bundlesToRemove)
            {
                if (_simulatedBundles.TryRemove(bundleBucket.Key, out var simulations))
                {
                    StopSimulations(simulations.Values);
                }

                foreach (MevBundle mevBundle in bundleBucket.Value)
                {
                    _bundles.TryRemove(mevBundle);
                }
            }
        }

        private void OnBundleRemoved(object? sender, SortedPool<MevBundle, MevBundle, long>.SortedPoolRemovedEventArgs e)
        {
            if (e.Evicted)
            {
                RemoveSimulation(e.Key);
            }
        }

        private void RemoveSimulation(MevBundle bundle)
        {
            if (_simulatedBundles.TryGetValue(bundle.BlockNumber, out ConcurrentDictionary<(MevBundle Bundle, Keccak BlockHash), SimulatedMevBundleContext>? simulations))
            {
                IEnumerable<(MevBundle Bundle, Keccak BlockHash)> keys = simulations.Keys.Where(k => Equals(k.Bundle, bundle));

                foreach ((MevBundle Bundle, Keccak BlockHash) key in keys)
                {
                    if (simulations.TryRemove(key, out var simulation))
                    {
                        StopSimulation(simulation);
                    }
                }

                if (simulations.Count == 0)
                {
                    _simulatedBundles.Remove(bundle.BlockNumber, out _);
                }
            }
        }

        private void StopSimulation(SimulatedMevBundleContext simulation)
        {
            if (!simulation.Task.IsCompleted)
            {
                simulation.CancellationTokenSource.Cancel();
            }
        }

        private void OnNewPendingMegabundle(object? sender, MegabundleEventArgs e)
        {
            _megabundles.TryRemove(e.RelayAddress, out _);
            RemoveSimulation(e.MevBundle);
        }

        async Task<IEnumerable<SimulatedMevBundle>> ISimulatedBundleSource.GetBundles(BlockHeader parent, UInt256 timestamp, long gasLimit, CancellationToken token)
        {
            HashSet<MevBundle> bundles = (await GetBundles(parent, timestamp, gasLimit, token)).ToHashSet();
            
            if (_simulatedBundles.TryGetValue(parent.Number, out ConcurrentDictionary<(MevBundle Bundle, Keccak BlockHash), SimulatedMevBundleContext>? simulatedBundlesForBlock))
            {
                IEnumerable<Task<SimulatedMevBundle>> resultTasks = simulatedBundlesForBlock
                    .Where(b => b.Key.BlockHash == parent.Hash)
                    .Where(b => bundles.Contains(b.Key.Bundle))
                    .Select(b => b.Value.Task)
                    .ToArray();
                
                await Task.WhenAny(Task.WhenAll(resultTasks), token.AsTask()); 

                IEnumerable<SimulatedMevBundle> res = resultTasks
                    .Where(t => t.IsCompletedSuccessfully)
                    .Select(t => t.Result)
                    .Where(t => t.Success)
                    .Where(s => s.GasUsed <= gasLimit); 
                
                return res;
            }
            return (Enumerable.Empty<SimulatedMevBundle>());
        }

        async Task<SimulatedMevBundle?> ISimulatedBundleSource.GetMegabundle(BlockHeader parent, UInt256 timestamp,
            long gasLimit, Address relayAddress, CancellationToken token)
        {
            MevBundle? bundle = await GetMegabundle(parent, timestamp, gasLimit, relayAddress, token);

            if (bundle is not null && _simulatedBundles.TryGetValue(parent.Number, out ConcurrentDictionary<(MevBundle Bundle, Keccak BlockHash), SimulatedMevBundleContext>? simulatedBundlesForBlock))
            {
                if (simulatedBundlesForBlock.TryGetValue((bundle, parent.Hash!),
                    out SimulatedMevBundleContext? simulatedBundle))
                {
                    Task<SimulatedMevBundle> resultTask = simulatedBundle.Task;
                    await Task.WhenAny(resultTask, token.AsTask());

                    bool success = resultTask.IsCompletedSuccessfully && resultTask.Result.Success;
                    bool withinGasLimit = resultTask.Result.GasUsed <= gasLimit;
                    if (success && withinGasLimit)
                    {
                        return resultTask.Result;
                    }
                }
            }
            return null;
        }

        public void Dispose()
        {
            _blockTree.NewHeadBlock -= OnNewBlock;
            _bundles.Removed -= OnBundleRemoved;
            NewPendingMegabundle -= OnNewPendingMegabundle;
        }

        protected class SimulatedMevBundleContext : IDisposable
        {
            public SimulatedMevBundleContext(Task<SimulatedMevBundle> task, CancellationTokenSource cancellationTokenSource)
            {
                Task = task;
                CancellationTokenSource = cancellationTokenSource;
            }
            
            public CancellationTokenSource CancellationTokenSource { get; }
            public Task<SimulatedMevBundle> Task { get; }

            public void Dispose()
            {
                CancellationTokenSource.Dispose();
            }
        }
    }
}
