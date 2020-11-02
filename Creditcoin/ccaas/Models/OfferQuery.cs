using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ccaas.Models
{
    public class OfferQuery
    {
        public string askOrderId { get; set; }
        public string bidOrderId { get; set; }
        public string expiration { get; set; }
    }
}
