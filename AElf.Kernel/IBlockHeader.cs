﻿using System;
using AElf.Kernel.Merkle;

namespace AElf.Kernel
{
    public interface IBlockHeader :  IHashProvider
    {
        int Version { get; set; }
        Hash PreviousHash { get; set; }
        Hash MerkleTreeRootOfTransactions { get; set; }
    }
}