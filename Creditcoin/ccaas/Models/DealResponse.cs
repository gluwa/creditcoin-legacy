using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ccaas.Models
{
    public class DealResponse
    {
        public string id { get; set; }
        public string block { get; set; }
        public string srcAddressId { get; set; }
        public string dstAddressId { get; set; }
        public Term term { get; set; }
        public string expiration { get; set; }
    }
}
