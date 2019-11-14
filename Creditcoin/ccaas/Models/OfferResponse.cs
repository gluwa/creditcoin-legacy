using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ccaas.Models
{
    public class OfferResponse
    {
        public string id;
        public string block;
        public OrderResponse askOrder, bidOrder;
        public string expiration;
    }
}
