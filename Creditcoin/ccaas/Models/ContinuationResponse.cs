using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ccaas.Models
{
    public class ContinuationResponse
    {
        public string reason { get; set; }
        public string waitingForeignBlockchain { get; set; }
        public string waitingCreditcoinCommit { get; set; }
    }
}
