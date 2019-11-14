using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

namespace ccbe.Models
{
    /// <summary>A Creditcoin block representation</summary>
    public class Block
    {
        /// <summary>The block's height</summary>
        public string BlockNum;
        /// <summary>The ID of consensus used to mine the block</summary>
        public string Consensus;
        /// <summary>The difficulty used to mine the block</summary>
        public string Difficulty;
        /// <summary>The nonce used to mine the block</summary>
        public string Nonce;
        /// <summary>The time when the block had been mined</summary>
        public string Timestamp;
        /// <summary>The reward for mining the block</summary>
        public string BlockReward;
        /// <summary>The version of Creditcoin transaction processor used to mine the block</summary>
        public string Version;
        /// <summary>An estimate of the block's size</summary>
        public string Size;
        /// <summary>The ID of the block immediately bellow in the blockchain</summary>
        public string PrevBlockId;
        /// <summary>The public key of the miner who mined the block</summary>
        public string SignerPubKey;
        /// <summary>The sighash of the miner who mined the block</summary>
        public string Sighash;
        /// <summary>A list of transactions in the block</summary>
        public Dictionary<string, Transaction> Transactions;
    }
}
