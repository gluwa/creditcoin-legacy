using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ccaas.Models
{
    public class LockQueryParam
    {
        public string key { get; set; }
        public LockQuery query { get; set; }
    }
}
