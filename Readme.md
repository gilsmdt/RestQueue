# RestQueue - C# queue wrapper around HttpClient
Useful for processing messages when the response is not important, just think of logging, auditing, ..

By using **RestQueue** to process your requests you get
* Improved performance by releasing the original caller thread, have the processing of the messages in the background
* Limited resources usage by queuing messages and reducing the number of connections needed
* Built-in retry mechanism
* Ability to add queue adjustment strategy to react for difference load and network conditions (see example below)
* Adjustable retry mechanism - [Exponential Backoff](https://en.wikipedia.org/wiki/Exponential_backoff) or fixed time

## Example usage ##

### Initialization ###
```cs
var restQueue = new RestQueue(new Uri("http://localhost:52159/api/"))
{
    RequestTimeout = TimeSpan.FromSeconds(10),
    RetryDelay = TimeSpan.FromMilliseconds(500),
    BatchSize = 100,
    BatchDelay = TimeSpan.FromSeconds(1)
};

```

### Queue some requests ###
```cs
for (int i = 0; i < 10000; i++)
{
    log = new Log()
    {
        Timestamp = DateTime.Now,
        Message = "test " + i
    };

    restQueue.Enqueue("logs/", log);
}
```

### Automatic Adjustment ###
By monitoring the statistics of the queue we can actually adjust in real time the configuration of the queue - batch size and delay between batches
```cs
/// <summary>
/// adjusts the batch delay according to success rate and thresholds
/// we're focusing on batch delay here but batch size can also be adjusted
/// </summary>
/// <param name="restQueue">the rest queue instance</param>
/// <param name="minBatchDelay">the minimum batch delay that would like to have, don't have this too low as it will consume more CPU</param>
/// <param name="maxBatchDelay">the maximum batch delay that we can afford</param>
/// <param name="minSuccessRate">minimum success rate to adjustment</param>
/// <param name="maxSuccessRate">maximum success rate to adjustment</param>
private static void AdjustQueue(RestQueue restQueue, double minBatchDelay, double maxBatchDelay, int minSuccessRate, int maxSuccessRate)
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
```

## License ##
[Apache License 2.0](https://github.com/tensorflow/tensorflow/blob/master/LICENSE)

