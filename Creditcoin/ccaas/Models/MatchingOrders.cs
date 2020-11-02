using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ccaas.Models
{
    public class MatchingOrders
    {
        public OrderResponse askOrder { get; set; }
        public OrderResponse bidOrder { get; set; }
}
}
