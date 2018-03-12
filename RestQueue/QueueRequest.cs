using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace RestQueue
{
    public class QueueRequest
    {
        public string Message { get; set; }
        public Uri RequestUri { get; set; }
        public HttpMethod Method { get; set; }
        internal int Attempts { get; set; }
    }
}
