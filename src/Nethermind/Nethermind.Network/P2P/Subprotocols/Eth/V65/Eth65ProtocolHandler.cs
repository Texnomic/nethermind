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

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Network.P2P.Subprotocols.Eth.V64;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.TxPool;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V65
{
    /// <summary>
    /// https://github.com/ethereum/EIPs/blob/master/EIPS/eip-2464.md
    /// </summary>
    public class Eth65ProtocolHandler : Eth64ProtocolHandler
    {
        private readonly IPooledTxsRequestor _pooledTxsRequestor;

        public Eth65ProtocolHandler(ISession session,
            IMessageSerializationService serializer,
            INodeStatsManager nodeStatsManager,
            ISyncServer syncServer,
            ITxPool txPool,
            IPooledTxsRequestor pooledTxsRequestor,
            ISpecProvider specProvider,
            ILogManager logManager)
            : base(session, serializer, nodeStatsManager, syncServer, txPool, specProvider, logManager)
        {
            _pooledTxsRequestor = pooledTxsRequestor;
        }

        public override string Name => "eth65";

        public override byte ProtocolVersion => 65;

        public override void HandleMessage(ZeroPacket message)
        {
            base.HandleMessage(message);
            switch (message.PacketType)
            {
                case Eth65MessageCode.PooledTransactions:
                    PooledTransactionsMessage pooledTxMsg
                        = Deserialize<PooledTransactionsMessage>(message.Content);
                    Metrics.Eth65PooledTransactionsReceived++;
                    ReportIn(pooledTxMsg);
                    Handle(pooledTxMsg);
                    break;
                case Eth65MessageCode.GetPooledTransactions:
                    GetPooledTransactionsMessage getPooledTxMsg
                        = Deserialize<GetPooledTransactionsMessage>(message.Content);
                    ReportIn(getPooledTxMsg);
                    Handle(getPooledTxMsg);
                    break;
                case Eth65MessageCode.NewPooledTransactionHashes:
                    NewPooledTransactionHashesMessage newPooledTxMsg =
                        Deserialize<NewPooledTransactionHashesMessage>(message.Content);
                    ReportIn(newPooledTxMsg);
                    Handle(newPooledTxMsg);
                    break;
            }
        }

        protected virtual void Handle(NewPooledTransactionHashesMessage msg)
        {
            Metrics.Eth65NewPooledTransactionHashesReceived++;
            Stopwatch stopwatch = Stopwatch.StartNew();

            _pooledTxsRequestor.RequestTransactions(Send, msg.Hashes.ToArray());
            
            stopwatch.Stop();
            if (Logger.IsTrace)
                Logger.Trace($"OUT {Counter:D5} {nameof(NewPooledTransactionHashesMessage)} to {Node:c} " +
                             $"in {stopwatch.Elapsed.TotalMilliseconds}ms");
        }

        private void Handle(GetPooledTransactionsMessage msg)
        {
            Metrics.Eth65GetPooledTransactionsReceived++;

            Stopwatch stopwatch = Stopwatch.StartNew();
            Send(FulfillPooledTransactionsRequest(msg));
            stopwatch.Stop();
            if (Logger.IsTrace)
                Logger.Trace($"OUT {Counter:D5} {nameof(GetPooledTransactionsMessage)} to {Node:c} " +
                             $"in {stopwatch.Elapsed.TotalMilliseconds}ms");
        }

        protected PooledTransactionsMessage FulfillPooledTransactionsRequest(
            GetPooledTransactionsMessage msg)
        {
            List<Transaction> txs = new();
            int responseSize = Math.Min(256, msg.Hashes.Count);
            for (int i = 0; i < responseSize; i++)
            {
                if (_txPool.TryGetPendingTransaction(msg.Hashes[i], out Transaction tx))
                {
                    txs.Add(tx);
                }
            }

            return new PooledTransactionsMessage(txs);
        }
        
        public override void SendNewTransactions(IEnumerable<Transaction> txs)
        {
            const int maxCapacity = 3200;
            List<Keccak> hashes = new(maxCapacity);

            foreach (Transaction tx in txs)
            {
                if (hashes.Count == maxCapacity)
                {
                    SendMessage(hashes);
                    hashes = new(maxCapacity);
                }
                
                if (tx.Hash is not null)
                {
                    hashes.Add(tx.Hash);
                    TxPool.Metrics.PendingTransactionsHashesSent++;
                }
            }

            if (hashes.Count > 0)
            {
                SendMessage(hashes);
            }
        }
        
        private void SendMessage(IList<Keccak> hashes)
        {
            NewPooledTransactionHashesMessage msg = new(hashes);
            Send(msg);
            Metrics.Eth65NewPooledTransactionHashesSent++;
        }
    }
}
