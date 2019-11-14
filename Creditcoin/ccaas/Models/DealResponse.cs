using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ccaas.Models
{
    public class DealResponse
    {
        public string id;
        public string block;
        public string srcAddressId;
        public string dstAddressId;
        public Term term;
        public string expiration;
    }
}
