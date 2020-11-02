using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ccaas.Models
{
    public class RegisterAddressQuery
    {
        public string blockchain { get; set; }
        public string address { get; set; }
        public string erc20 { get; set; }
        public string network { get; set; }
    }
}
