using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ccbe.Models
{
    /// <summary>A Creditcoin Transaction representation</summary>
    public class Transaction
    {
        /// <summary>The ID of the transaction processor</summary>
        public string FamilyName { get; set; }
        /// <summary>The version of the transaction processor</summary>
        public string FamilyVersion { get; set; }
        /// <summary>The data passed to the transaction processor</summary>
        public string Payload { get; set; }
        /// <summary>The public key of the transaction originator</summary>
        public string SignerPubKey { get; set; }
        /// <summary>The sighash of the transaction originator</summary>
        public string Sighash { get; set; }
    }
}
