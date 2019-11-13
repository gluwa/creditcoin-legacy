using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ccaas.Models
{
    public class ContinuationResponse
    {
        public string reason;
        public string waitingForeignBlockchain;
        public string waitingCreditcoinCommit;
    }
}
