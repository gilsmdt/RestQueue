using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RestQueue
{
    public enum QueueRetryStrategy
    {
        /// <summary>
        /// a fixed time defined by RetryDelay will be used
        /// </summary>
        Fixed,

        /// <summary>
        /// the time to wait between retries will be set as 2 ^ RetryNumber
        /// </summary>
        ExponentialBackoff
    }
}
