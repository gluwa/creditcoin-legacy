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
        public string BlockNum { get; set; }
        /// <summary>The ID of consensus used to mine the block</summary>
        public string Consensus { get; set; }
        /// <summary>The difficulty used to mine the block</summary>
        public string Difficulty { get; set; }
        /// <summary>The nonce used to mine the block</summary>
        public string Nonce { get; set; }
        /// <summary>The time when the block had been mined</summary>
        public string Timestamp { get; set; }
        /// <summary>The reward for mining the block</summary>
        public string BlockReward { get; set; }
        /// <summary>The version of Creditcoin transaction processor used to mine the block</summary>
        public string Version { get; set; }
        /// <summary>An estimate of the block's size</summary>
        public string Size { get; set; }
        /// <summary>The ID of the block immediately bellow in the blockchain</summary>
        public string PrevBlockId { get; set; }
        /// <summary>The public key of the miner who mined the block</summary>
        public string SignerPubKey { get; set; }
        /// <summary>The sighash of the miner who mined the block</summary>
        public string Sighash { get; set; }
        /// <summary>A list of transactions in the block</summary>
        public Dictionary<string, Transaction> Transactions { get; set; }
    }
}
