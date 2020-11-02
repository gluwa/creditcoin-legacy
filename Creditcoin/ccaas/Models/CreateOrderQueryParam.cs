using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ccaas.Models
{
    public class CreateOrderQueryParam
    {
        public string key { get; set; }
        public OrderQuery query { get; set; }
    }
}
