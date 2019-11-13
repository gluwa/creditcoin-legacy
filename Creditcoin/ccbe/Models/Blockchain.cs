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
        public string BlockHeight;
        /// <summary>The current difficulty used to mine blocks in the Creditcoin blockchain</summary>
        public string Difficulty;
        /// <summary>The current reward for mining blocks in the Creditcoin blockchain</summary>
        public string BlockReward;
        /// <summary>The current transaction fee in the Creditcoin blockchain</summary>
        public string TrnsactionFee;
        /// <summary>The current amount of Creditcoins in Credos in the Creditcoin blockchain excluding withheld transaction fees</summary>
        public string CirculationSupply;
        /// <summary>The current hashpower of the Creditcoin network</summary>
        public string NetworkWeight;
    }
}
