using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ccbe.Models
{
    /// <summary>A Creditcoin blockchain info</summary>
    public class Blockchain
    {
        /// <summary>The number of blocks currently in the Creditcoin blockchain</summary>
        public string BlockHeight { get; set; }
        /// <summary>The current difficulty used to mine blocks in the Creditcoin blockchain</summary>
        public string Difficulty { get; set; }
        /// <summary>The current reward for mining blocks in the Creditcoin blockchain</summary>
        public string BlockReward { get; set; }
        /// <summary>The current transaction fee in the Creditcoin blockchain (obsolete, please use TransactionFee)</summary>
        public string TrnsactionFee { get; set; }
        /// <summary>The current transaction fee in the Creditcoin blockchain</summary>
        public string TransactionFee { get; set; }
        /// <summary>The current amount of Creditcoins in Credos in the Creditcoin blockchain excluding withheld transaction fees</summary>
        public string CirculationSupply { get; set; }
        /// <summary>The current hashpower of the Creditcoin network</summary>
        public string NetworkWeight { get; set; }
        /// <summary>Amount of CTC coins in circulation currently</summary>
        public string CtcInCirculation { get; set; }
    }
}
