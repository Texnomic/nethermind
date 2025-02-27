﻿//  Copyright (c) 2021 Demerzel Solutions Limited
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

using FluentAssertions;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.AuRa.Test.Validators
{
    public class ValidatorInfoDecoderTests
    {
        [Test]
        public void Can_decode_previously_encoded()
        {
            ValidatorInfo info = new(10, 5, new[] {TestItem.AddressA, TestItem.AddressC});
            Rlp rlp = Rlp.Encode(info);
            ValidatorInfo decoded = Rlp.Decode<ValidatorInfo>(rlp);
            decoded.Should().BeEquivalentTo(info);
        }
    }
}
