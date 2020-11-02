using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ccaas.Models
{
    public class TransferQuery
    {
        public string ethKey { get; set; }
        public string gain { get; set; }
        public string dealOrderId { get; set; }
        public string txid { get; set; }
        public string fee { get; set; }
        public string continuation { get; set; }
    }
}
