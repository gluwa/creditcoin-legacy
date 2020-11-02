using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ccbe.Models
{
    /// <summary>The representation of a segment of an object sequence</summary>
    public class Pagination
    {
        /// <summary>A segment of an object sequence</summary>
        public object Data { get; set; }
        /// <summary>A URL to retrieve a next segment of the sequence</summary>
        public string Next { get; set; }
    }
}
