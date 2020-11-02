using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ccaas.Models
{
    public class OfferResponse
    {
        public string id { get; set; }
        public string block { get; set; }
        public OrderResponse askOrder { get; set; }
        public OrderResponse bidOrder { get; set; }
        public string expiration { get; set; }
    }
}
