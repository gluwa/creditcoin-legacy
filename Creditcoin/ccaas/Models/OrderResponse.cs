using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ccaas.Models
{
    public class OrderResponse
    {
        public string id { get; set; }
        public string block { get; set; }
        public OrderQuery order { get; set; }
    }
}
