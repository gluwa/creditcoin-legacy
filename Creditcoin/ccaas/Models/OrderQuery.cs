using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ccaas.Models
{
    public class OrderQuery
    {
        public string addressId { get; set; }
        public Term term { get; set; }
        public string expiration { get; set; }
    }
}
