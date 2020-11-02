using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ccaas.Models
{
    public class SendFundsQuery
    {
        public string amount { get; set; }
        public string destinationSighash { get; set; }
    }
}
