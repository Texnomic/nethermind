/*
 * Copyright (c) 2021 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture][Parallelizable(ParallelScope.All)]
    public class MetaTests
    {
        private List<string> excludesDirectories = new List<string>()
        {
            "stEWASMTests",
            "VMTests",
            "Specs",
            "runtimes",
            "ref",
            "TestFiles",
            "Blockhash"
        };
        
        [Test]
        public void All_categories_are_tested()
        {
            string[] directories = Directory.GetDirectories(AppDomain.CurrentDomain.BaseDirectory)
                .Select(Path.GetFileName)
                .ToArray();
            Type[] types = GetType().Assembly.GetTypes();
            List<string> missingCategories = new List<string>();
            foreach (string directory in directories)
            {
                string expectedTypeName = ExpectedTypeName(directory);
                Type type = types.SingleOrDefault(t => string.Equals(t.Name, expectedTypeName, StringComparison.InvariantCultureIgnoreCase));
                if(type == null && !excludesDirectories.Contains(directory))
                {
                    if (new DirectoryInfo(directory).GetFiles().Any(f => f.Name.Contains(".resources.")))
                    {
                        continue;
                    }
                    
                    missingCategories.Add(directory + " - " + expectedTypeName);
                }
            }

            foreach (string missing in missingCategories)
            {
                Console.WriteLine($"{missing} category is missing");
            }

            Assert.AreEqual(0, missingCategories.Count);
        }

        private static string ExpectedTypeName(string directory)
        {
            string expectedTypeName = directory.Remove(0, 2);
            if (!expectedTypeName.EndsWith("Tests"))
            {
                if (!expectedTypeName.EndsWith("Test"))
                {
                    expectedTypeName += "Tests";
                }
                else
                {
                    expectedTypeName += "s";    
                }
            }

            if (directory.StartsWith("vm"))
            {
                return "Vm" + expectedTypeName;    
            }
            
            return expectedTypeName;
        }
    }
}
