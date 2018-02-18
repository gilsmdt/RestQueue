using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CLI
{
    class Program
    {
        private static readonly HttpMessageHandler handler = null;
        private static int errorCount = 0;
        private static int httpErrorCount = 0;
        private static int successfulCount = 0;

        static void Main(string[] args)
        {
            Log log;

            // we'll give a head start to the server so it'll be ready before the first request
            Thread.Sleep(2000);

            var restQueue = new RestQueue.RestQueue(new Uri("http://localhost:52159/api/"), handler)
            {
                RequestTimeout = TimeSpan.FromSeconds(10),
                RetryDelay = TimeSpan.FromMilliseconds(500),
                BatchSize = 100,
                BatchDelay = TimeSpan.FromSeconds(1)
            };

            // event listeners
            restQueue.OnRequestError += (ex, timeout) =>
            {
                if (ex != null && !string.IsNullOrEmpty(ex.Message))
                {
                    Trace.WriteLine(ex.Message);
                }

                Interlocked.Increment(ref errorCount);
            };

            restQueue.OnHttpError += statusCode => Interlocked.Increment(ref httpErrorCount);
            restQueue.OnRequestSent += () => Interlocked.Increment(ref successfulCount);

            // add requests to queue
            Task.Run(() =>
            {
                for (int i = 0; i < 10000; i++)
                {
                    log = new Log()
                    {
                        Timestamp = DateTime.Now,
                        Message = "test " + i
                    };

                    restQueue.Enqueue("logs/", log);
                }

                Trace.WriteLine("added all requests to queue");
            });

            // trace a status every second
            Task.Run(() =>
            {
                while (true)
                {
                    Trace.WriteLine($"success rate: {GetSuccessRate()}%, " +
                        $"pending: {restQueue.Pending}, " +
                        $"successful: {successfulCount}, " +
                        $"retry: {errorCount + httpErrorCount}, " +
                        $"errors: {errorCount}, " +
                        $"HTTP errors: { httpErrorCount}, " +
                        $"requests in queue { restQueue.Count}");

                    Thread.Sleep(1000);
                }
            });

            // adjust the queue settings automatically according to performance
            Task.Run(() =>
            {
                while (true)
                {
                    // we're intentionally failing some of the request on the server to simulate real usage so the highest possible success rate for this test is about %80
                    AdjustQueue(restQueue, 100, 60000, 95, 75);

                    // sample the queue again after half of the delay has taken place to see if anything approved
                    // we want to gradually react to changes
                    Thread.Sleep((int)restQueue.BatchDelay.TotalMilliseconds / 2);
                }
            });

            Console.ReadKey();

            Trace.WriteLine("stopping processing..");
            restQueue.Stop();
            Trace.WriteLine($"done");
        }

        /// <summary>
        /// calculates the success rate
        /// </summary>
        /// <returns></returns>
        private static int GetSuccessRate()
        {
            int rate = 0;

            if (successfulCount > 0)
            {
                rate = (int)(successfulCount * 100 / (successfulCount + errorCount + httpErrorCount));
            }

            return rate;
        }

        /// <summary>
        /// adjusts the batch delay according to success rate and thresholds
        /// we're focusing on batch delay here but batch size can also be adjusted
        /// </summary>
        /// <param name="restQueue">the rest queue instance</param>
        /// <param name="minBatchDelay">the minimum batch delay that would like to have, don't have this too low as it will consume more CPU</param>
        /// <param name="maxBatchDelay">the maximum batch delay that we can afford</param>
        /// <param name="minSuccessRate">minimum success rate to adjustment</param>
        /// <param name="maxSuccessRate">maximum success rate to adjustment</param>
        private static void AdjustQueue(RestQueue.RestQueue restQueue, double minBatchDelay, double maxBatchDelay, int minSuccessRate, int maxSuccessRate)
        {
            double batchDelay = restQueue.BatchDelay.TotalMilliseconds;

            if (successfulCount > 0)
            {
                var rate = GetSuccessRate();

                // if we're below the success rate that we're looking for and the batch delay can be adjusted then try to increase it by 10%
                if (rate < minSuccessRate && batchDelay < maxBatchDelay)
                {
                    Trace.WriteLine("success rate too low, increasing batch delay");
                    // increase delay
                    restQueue.BatchDelay = TimeSpan.FromMilliseconds(Math.Min((batchDelay + maxBatchDelay) / 2, maxBatchDelay));
                }

                // if we're above the success rate that we're looking for, we have pending requests and the batch delay can be adjusted then try to decrease it by 10%
                if (rate > maxSuccessRate && restQueue.Pending > 0 && batchDelay < minBatchDelay)
                {
                    Trace.WriteLine("success rate too high, decreasing batch delay");

                    // decrease delay
                    restQueue.BatchDelay = TimeSpan.FromMilliseconds(Math.Max((batchDelay + minBatchDelay) / 2, minBatchDelay));
                }
            }
        }
    }
}
