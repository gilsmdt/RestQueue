using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace RestQueue
{
    public class RestQueue : IDisposable
    {
        private const int DEFAULT_MAX_QUEUE_SIZE = 100000;
        private const int DEFAULT_EMPTY_QUEUE_DELAY = 100;
        private const double DEFAULT_TIMEOUT = 60;
        private const double DEFAULT_RETRY_DELAY = 3;
        private const int DEFAULT_BATCH_SIZE = 100;
        private const int DEFAULT_BATCH_DELAY = 100;
        private const int DEFAULT_MAX_ATTEMPTS = 3;

        /// <summary>
        /// sync object to limit the size of the queue
        /// </summary>
        private readonly object syncObject = new object();

        public delegate void RequestSentHandler();
        public delegate void ErrorHandler(Exception ex);
        public delegate void RequestErrorHandler(Exception ex, bool isTimeout);
        public delegate void HttpErrorHandler(HttpStatusCode statusCode);
        public delegate string ContentFormatter(object obj);

        /// <summary>
        /// will be called when a general error that the queue cannot recover from will happen, the processing will be stopped automatically
        /// </summary>
        public event ErrorHandler OnError;

        /// <summary>
        /// will be called when an exception will be thrown from the HTTP client, usually a timeout but can be other network issue
        /// </summary>
        public event RequestErrorHandler OnRequestError;

        /// <summary>
        /// will be called if the server returns an unexpected HTTP response like Internal Server Error 500
        /// </summary>
        public event HttpErrorHandler OnHttpError;

        /// <summary>
        /// will be called on a successful processing of a request
        /// </summary>
        public event RequestSentHandler OnRequestSent;

        /// <summary>
        /// keeps all requests waiting to be sent
        /// </summary>
        private ConcurrentQueue<RestQueueRequest> requests = new ConcurrentQueue<RestQueueRequest>();

        /// <summary>
        /// HTTP client that will be used to process requests
        /// </summary>
        private HttpClient httpClient;

        /// <summary>
        /// will be set to true when processing has started
        /// </summary>
        private volatile bool processing = false;

        /// <summary>
        /// will be set to true when processing has been stopped
        /// </summary>
        private volatile bool stopped = true;

        /// <summary>
        /// holds the number of requests that were sent for the current batch
        /// </summary>
        private int batchIndex = 0;

        /// <summary>
        /// keeps the number of requests sent and still waiting for an answer
        /// </summary>
        private int pending = 0;

        /// <summary>
        /// will be used to create the StringContent and identified the content type
        /// </summary>
        private string mediaType;

        /// <summary>
        /// returns the number of requests in the queue waiting for a response
        /// </summary>
        public int Pending { get { return pending; } }

        /// <summary>
        /// returns the number of requests in the queue waiting to be sent to HttpClient
        /// </summary>
        public int Count { get { return requests.Count; } }

        /// <summary>
        /// sets the timeout for the requests, default is 60s
        /// </summary>
        public TimeSpan RequestTimeout { get { return httpClient.Timeout; } set { httpClient.Timeout = value; } }

        /// <summary>
        /// on failures determine how much time to wait before retry, default is 3s
        /// </summary>
        public TimeSpan FixedRetryDelay { get; set; } = TimeSpan.FromSeconds(DEFAULT_RETRY_DELAY);

        /// <summary>
        /// defines how many attempts a request should have, default is 3
        /// </summary>
        public int MaxAttempts { get; set; } = DEFAULT_MAX_ATTEMPTS;

        /// <summary>
        /// determine which strategy to choose between retries
        /// </summary>
        public QueueRetryStrategy RetryStrategy { get; set; } = QueueRetryStrategy.ExponentialBackoff;

        /// <summary>
        /// if the queue is empty indicate how much time to wait before checking for a new request, default is 100ms
        /// </summary>
        public int EmptyQueueDelay { get; set; } = DEFAULT_EMPTY_QUEUE_DELAY;

        /// <summary>
        /// limits the size of the queue in case of an error as we don't want to use too many resources, default is 100,000
        /// </summary>
        public int MaxQueueSize { get; set; } = DEFAULT_MAX_QUEUE_SIZE;

        /// <summary>
        /// holds the formatter for objects
        /// </summary>
        public ContentFormatter Formatter { get; set; }

        /// <summary>
        /// formats an object with JSON serializer
        /// </summary>
        private ContentFormatter JsonFormatter = obj => JsonConvert.SerializeObject(obj);

        /// <summary>
        /// Indicate how many requests to send before taking a break
        /// </summary>
        public int BatchSize { get; set; } = DEFAULT_BATCH_SIZE;

        /// <summary>
        /// Indicate how many milliseconds to wait before sending the next batch of requests
        /// </summary>
        public TimeSpan BatchDelay { get; set; } = TimeSpan.FromMilliseconds(DEFAULT_BATCH_DELAY);

        private Func<int, TimeSpan> CalculateExponentialBackoff = (attempts) => TimeSpan.FromSeconds(Math.Pow(2, attempts));

        /// <summary>
        /// holds a request queue to a REST API with JSON format
        /// the purpose of this queue is to handle async requests where the response content is not important, for example: logging, auditing, ...
        /// there is no way to get the response for the requests but there will be an internal verification on the HTTP response code to be "200 OK", "201 Created" or "202 Accepted"
        /// this is ideal behavior for logging where we just want to indicate that the message was received and the processing will be done in asynchronous manner
        /// you should create a single instance (singleton lifetime) of this class for each base address
        /// the processing will be started automatically
        /// </summary>
        /// <param name="baseAddress">the base address that will be prefixed for all requests, should end with /</param>
        /// <param name="handler">optional - if you have to do some pre / post processing</param>        
        /// <param name="mediaType">optional - specify which media type will be used for identifying the content sent, default is "application/json"</param>                
        /// <param name="defaultRequestHeaders ">optional - specify which headers will be sent with every request, default is "Accept", "application/json"</param>                
        public RestQueue(Uri baseAddress, HttpMessageHandler handler = null, string mediaType = "application/json", Dictionary<string, string> defaultRequestHeaders = null)
        {
            if (!baseAddress.ToString().EndsWith("/"))
                throw new Exception("baseAddress must end with /\r\nplease read the following link for more details\r\nhttps://stackoverflow.com/questions/23438416/why-is-httpclient-baseaddress-not-working");

            this.mediaType = mediaType;
            Formatter = JsonFormatter;

            httpClient = handler == null ? new HttpClient() : new HttpClient(handler, true);
            httpClient.BaseAddress = baseAddress;
            httpClient.Timeout = TimeSpan.FromSeconds(DEFAULT_TIMEOUT);

            if (defaultRequestHeaders == null)
            {
                defaultRequestHeaders = new Dictionary<string, string>()
                {
                    { "Accept", "application/json" }
                };
            }

            foreach (var header in defaultRequestHeaders)
            {
                httpClient.DefaultRequestHeaders.Add(header.Key, header.Value);
            }

            // setup the HTTP client to renew connections after one minute to be able to handle DNS changes
            ServicePointManager.FindServicePoint(baseAddress).ConnectionLeaseTimeout = (int)TimeSpan.FromMinutes(1).TotalMilliseconds;
            ServicePointManager.DnsRefreshTimeout = (int)TimeSpan.FromMinutes(1).TotalMilliseconds;

            Start();
        }

        /// <summary>
        /// starts processing requests in a background thread
        /// </summary>
        public void Start()
        {
            if (!processing && stopped)
            {
                processing = true;
                stopped = false;

                Task.Run((Action)Process);
            }
        }

        /// <summary>
        /// Enqueue an object to be sent, the object will be serialized to JSON before sending
        /// </summary>
        /// <param name="uri">Uri to be used, shouldn't start with /</param>
        /// <param name="obj">the object to send, if object is null the request will be ignore</param>
        public void Enqueue(string uri, object obj)
        {
            if (obj != null)
            {
                var message = Formatter(obj);

                Enqueue(uri, message);
            }
        }

        /// <summary>
        /// Enqueue an object to be sent
        /// </summary>
        /// <param name="uri">Uri to be used, shouldn't start with / and will be prefixed with the base address</param>
        /// <param name="message">the JSON message to send, if null is passed HTTP GET will be used</param>
        public void Enqueue(string uri, string message = null)
        {
            RestQueueRequest request = new RestQueueRequest()
            {
                Message = message,
                RequestUri = new Uri(httpClient.BaseAddress.OriginalString + uri),
                Method = message == null ? HttpMethod.Get : HttpMethod.Post,
                Attempts = 0
            };

            requests.Enqueue(request);

            // throw away old messages in case the queue is full
            lock (syncObject)
            {
                while (requests.Count > MaxQueueSize)
                {
                    requests.TryDequeue(out request);
                }
            }
        }

        /// <summary>
        /// decides if and when to retry sending the request
        /// </summary>
        /// <param name="request">the request object</param>
        private async void Retry(RestQueueRequest request)
        {
            request.Attempts++;

            if (request.Attempts < MaxAttempts)
            {
                // add delay before the retry
                await Task.Delay(RetryStrategy == QueueRetryStrategy.ExponentialBackoff ? CalculateExponentialBackoff(request.Attempts) : FixedRetryDelay);

                // re-add the request into the queue
                requests.Enqueue(request);
            }
        }

        /// <summary>
        /// taking request from the queue and send them to the server
        /// on a successful transfer the request will be removed from the queue
        /// </summary>
        private void Process()
        {
            RestQueueRequest request;
            HttpRequestMessage httpRequest;

            while (processing)
            {
                try
                {
                    // allow stop processing before batch complete
                    if (!processing)
                        break;

                    if (requests.TryDequeue(out request))
                    {
                        Interlocked.Increment(ref pending);
                        Interlocked.Increment(ref batchIndex);

                        httpRequest = new HttpRequestMessage()
                        {
                            Content = new StringContent(request.Message, Encoding.UTF8, mediaType),
                            Method = request.Method,
                            RequestUri = request.RequestUri
                        };

                        httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead)
                            .ContinueWith(HandleResponse, request);

                        // we've reached the max number of requests in the batch, we'll start a new batch after the delay
                        if (batchIndex >= BatchSize)
                        {
                            Interlocked.Exchange(ref batchIndex, 0);
                            Thread.Sleep(BatchDelay);
                        }
                    }
                    else
                    {
                        // zero the batch counter as we've reached the end of the queue
                        Interlocked.Exchange(ref batchIndex, 0);

                        // queue is empty, we'll suspend our next check
                        Thread.Sleep(EmptyQueueDelay);
                    }
                }
                catch (Exception ex)
                {
                    // we've encountered an error that we didn't predict, we'll stop the processing
                    processing = false;
                    Trace.WriteLine("error on queue processing " + ex.Message);

                    OnError?.Invoke(ex);
                    break;
                }
            }

            stopped = true;
        }

        /// <summary>
        /// in case of a failure will enqueue the failed request again
        /// </summary>
        /// <param name="task"></param>
        /// <param name="state"></param>
        private void HandleResponse(Task<HttpResponseMessage> task, object state)
        {
            Interlocked.Decrement(ref pending);

            if (!task.IsCompleted || task.Status != TaskStatus.RanToCompletion)
            {
                Retry(state as RestQueueRequest);
                OnRequestError?.Invoke(task.Exception, task.Status == TaskStatus.Canceled);
            }
            else if (task.Result.StatusCode != HttpStatusCode.OK &&
                    task.Result.StatusCode != HttpStatusCode.Created &&
                    task.Result.StatusCode != HttpStatusCode.Accepted)
            {
                Retry(state as RestQueueRequest);
                OnHttpError?.Invoke(task.Result.StatusCode);
            }
            else
            {
                OnRequestSent?.Invoke();
            }
        }

        /// <summary>
        /// stop the processing of the requests in the queue, the remaining messages in the queue will not be cleared
        /// </summary>
        public void Stop()
        {
            if (processing)
            {
                processing = false;

                // block the thread until we can confirm the processing has completed
                while (!stopped)
                {
                    Thread.Sleep(EmptyQueueDelay);
                }
            }
        }

        /// <summary>
        /// cleanup..
        /// </summary>
        public void Dispose()
        {
            httpClient.Dispose();
        }
    }
}