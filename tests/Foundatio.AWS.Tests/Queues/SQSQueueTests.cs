using System;
using System.Threading.Tasks;
using Foundatio.Queues;
using Foundatio.Tests.Queue;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.AWS.Tests.Queues;

public class SQSQueueTests : QueueTestBase
{
    private readonly string _queueName = "foundatio-" + Guid.NewGuid().ToString("N").Substring(10);

    public SQSQueueTests(ITestOutputHelper output) : base(output)
    {
        // SQS queue stats are approximate and unreliable
        _assertStats = false;
    }

    protected override IQueue<SimpleWorkItem> GetQueue(int retries = 1, TimeSpan? workItemTimeout = null, TimeSpan? retryDelay = null, int[] retryMultipliers = null, int deadLetterMaxItems = 100, bool runQueueMaintenance = true, TimeProvider timeProvider = null)
    {
        var queue = new SQSQueue<SimpleWorkItem>(
            o => o.ConnectionString("serviceurl=http://localhost:4566;AccessKey=xxx;SecretKey=xxx")
                .Name(_queueName)
                .Retries(retries)
                .RetryDelay(attempt =>
                {
                    int[] multipliers = retryMultipliers ?? [1, 3, 5, 10];
                    int index = Math.Min(attempt, multipliers.Length - 1);
                    return TimeSpan.FromSeconds(multipliers[index]);
                })
                .WorkItemTimeout(workItemTimeout.GetValueOrDefault(TimeSpan.FromMinutes(5)))
                .DequeueInterval(TimeSpan.FromSeconds(1))
                .ReadQueueTimeout(TimeSpan.FromSeconds(1))
                .TimeProvider(timeProvider)
                .LoggerFactory(Log));

        _logger.LogDebug("Queue Id: {QueueId}", queue.QueueId);
        return queue;
    }

    protected IQueue<SimpleWorkItem> GetQueue(int retries = 1, TimeSpan? workItemTimeout = null, TimeSpan? retryDelay = null, int[] retryMultipliers = null, int deadLetterMaxItems = 100, bool runQueueMaintenance = true, TimeSpan? dequeueInterval = null, TimeSpan? readQueueTimeout = null)
    {
        var queue = new SQSQueue<SimpleWorkItem>(
            o => o.ConnectionString("serviceurl=http://localhost:4566;AccessKey=xxx;SecretKey=xxx")
                .Name(_queueName)
                .Retries(retries)
                .RetryDelay(attempt =>
                {
                    int[] multipliers = retryMultipliers ?? [1, 3, 5, 10];
                    int index = Math.Min(attempt, multipliers.Length - 1);
                    return TimeSpan.FromSeconds(multipliers[index]);
                })
                .WorkItemTimeout(workItemTimeout.GetValueOrDefault(TimeSpan.FromMinutes(5)))
                .DequeueInterval(dequeueInterval ?? TimeSpan.FromSeconds(1))
                .ReadQueueTimeout(readQueueTimeout ?? TimeSpan.FromSeconds(1))
                .LoggerFactory(Log));

        _logger.LogDebug("Queue Id: {QueueId}", queue.QueueId);
        return queue;
    }

    [Fact]
    public override Task CanQueueAndDequeueWorkItemAsync()
    {
        return base.CanQueueAndDequeueWorkItemAsync();
    }

    [Fact]
    public override Task CanQueueAndDequeueWorkItemWithDelayAsync()
    {
        return base.CanQueueAndDequeueWorkItemWithDelayAsync();
    }

    [Fact]
    public override Task CanUseQueueOptionsAsync()
    {
        return base.CanUseQueueOptionsAsync();
    }

    [Fact]
    public override Task CanDiscardDuplicateQueueEntriesAsync()
    {
        return base.CanDiscardDuplicateQueueEntriesAsync();
    }

    [Fact]
    public override Task CanDequeueWithCancelledTokenAsync()
    {
        return base.CanDequeueWithCancelledTokenAsync();
    }

    [Fact]
    public override Task CanDequeueEfficientlyAsync()
    {
        return base.CanDequeueEfficientlyAsync();
    }

    [Fact]
    public override Task CanResumeDequeueEfficientlyAsync()
    {
        return base.CanResumeDequeueEfficientlyAsync();
    }

    [Fact]
    public override Task CanQueueAndDequeueMultipleWorkItemsAsync()
    {
        return base.CanQueueAndDequeueMultipleWorkItemsAsync();
    }

    [Fact]
    public override Task WillNotWaitForItemAsync()
    {
        return base.WillNotWaitForItemAsync();
    }

    [Fact]
    public override Task WillWaitForItemAsync()
    {
        return base.WillWaitForItemAsync();
    }

    [Fact]
    public override Task DequeueWaitWillGetSignaledAsync()
    {
        return base.DequeueWaitWillGetSignaledAsync();
    }

    [Fact]
    public override Task CanUseQueueWorkerAsync()
    {
        return base.CanUseQueueWorkerAsync();
    }

    [Fact]
    public override Task CanHandleErrorInWorkerAsync()
    {
        return base.CanHandleErrorInWorkerAsync();
    }

    [Fact]
    public override Task WorkItemsWillTimeoutAsync()
    {
        return base.WorkItemsWillTimeoutAsync();
    }

    [Fact]
    public override Task WorkItemsWillGetMovedToDeadletterAsync()
    {
        return base.WorkItemsWillGetMovedToDeadletterAsync();
    }

    [Fact]
    public override Task CanAutoCompleteWorkerAsync()
    {
        return base.CanAutoCompleteWorkerAsync();
    }

    [Fact]
    public override Task CanHaveMultipleQueueInstancesAsync()
    {
        return base.CanHaveMultipleQueueInstancesAsync();
    }

    [Fact]
    public override Task CanDelayRetryAsync()
    {
        return base.CanDelayRetryAsync();
    }

    [Fact]
    public override Task CanRunWorkItemWithMetricsAsync()
    {
        return base.CanRunWorkItemWithMetricsAsync();
    }

    [Fact]
    public override Task CanRenewLockAsync()
    {
        return base.CanRenewLockAsync();
    }

    [Fact]
    public override Task CanAbandonQueueEntryOnceAsync()
    {
        return base.CanAbandonQueueEntryOnceAsync();
    }

    [Fact]
    public override Task CanCompleteQueueEntryOnceAsync()
    {
        return base.CanCompleteQueueEntryOnceAsync();
    }

    [Fact]
    public override Task CanDequeueWithLockingAsync()
    {
        return base.CanDequeueWithLockingAsync();
    }

    [Fact]
    public override Task CanHaveMultipleQueueInstancesWithLockingAsync()
    {
        return base.CanHaveMultipleQueueInstancesWithLockingAsync();
    }

    [Fact]
    public override Task MaintainJobNotAbandon_NotWorkTimeOutEntry()
    {
        return base.MaintainJobNotAbandon_NotWorkTimeOutEntry();
    }

    [Fact]
    public override Task VerifyRetryAttemptsAsync()
    {
        return base.VerifyRetryAttemptsAsync();
    }

    [Fact]
    public override Task VerifyDelayedRetryAttemptsAsync()
    {
        return base.VerifyDelayedRetryAttemptsAsync();
    }

    [Fact(Skip = "SQS Queues has no queue stats for abandoned, it just increments the queued count and decrements the working count. Only the entry attribute ApproximateNumberOfMessages is available.")]
    public override Task CanHandleAutoAbandonInWorker()
    {
        return base.CanHandleAutoAbandonInWorker();
    }

    [Fact]
    public void RetryBackoff()
    {
        var options = new SQSQueueOptions<SimpleWorkItem>();
        var backoff1 = options.RetryDelay(1);
        Assert.InRange(backoff1, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));
        var backoff2 = options.RetryDelay(2);
        Assert.InRange(backoff2, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5));
        var backoff3 = options.RetryDelay(3);
        Assert.InRange(backoff3, TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(9));
        var backoff4 = options.RetryDelay(4);
        Assert.InRange(backoff4, TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(17));
        var backoff5 = options.RetryDelay(5);
        Assert.InRange(backoff5, TimeSpan.FromSeconds(32), TimeSpan.FromSeconds(33));
        var backoff6 = options.RetryDelay(6);
        Assert.InRange(backoff6, TimeSpan.FromSeconds(64), TimeSpan.FromSeconds(65));
        var backoff7 = options.RetryDelay(7);
        Assert.InRange(backoff7, TimeSpan.FromSeconds(128), TimeSpan.FromSeconds(129));
        var backoff8 = options.RetryDelay(8);
        Assert.InRange(backoff8, TimeSpan.FromSeconds(256), TimeSpan.FromSeconds(257));
        var backoff9 = options.RetryDelay(9);
        Assert.InRange(backoff9, TimeSpan.FromSeconds(512), TimeSpan.FromSeconds(513));
        var backoff10 = options.RetryDelay(10);
        Assert.InRange(backoff10, TimeSpan.FromSeconds(1024), TimeSpan.FromSeconds(1025));
    }

    [Fact]
    public async Task CanGetQueueItemWithDeliveryDelayAndEnsureMessageNotMarkedWorkingWhileWaiting()
    {
        var queue = GetQueue(dequeueInterval: TimeSpan.Zero, readQueueTimeout: TimeSpan.FromSeconds(2));
        if (queue == null)
            return;

        try
        {
            await queue.DeleteQueueAsync();
            // await AssertEmptyQueueAsync(queue); // TODO: Uncomment once foundatio is updated.

            await queue.EnqueueAsync(new SimpleWorkItem { Data = "Hello" }, new QueueEntryOptions { DeliveryDelay = TimeSpan.FromSeconds(4) });
            var workItem = await queue.DequeueAsync(TimeSpan.FromSeconds(2));
            Assert.Null(workItem);

            if (_assertStats)
            {
                var stats = await queue.GetQueueStatsAsync();
                Assert.Equal(0, stats.Dequeued);
                Assert.Equal(1, stats.Enqueued);
                Assert.Equal(0, stats.Queued);
                Assert.Equal(0, stats.Working);
            }

            await Task.Delay(TimeSpan.FromSeconds(3));

            if (_assertStats)
            {
                var stats = await queue.GetQueueStatsAsync();
                Assert.Equal(0, stats.Dequeued);
                Assert.Equal(1, stats.Enqueued);
                Assert.Equal(1, stats.Queued);
                Assert.Equal(0, stats.Working);
            }

            _logger.LogInformation("Second Dequeue Attempt");
            workItem = await queue.DequeueAsync(TimeSpan.FromSeconds(2));
            Assert.NotNull(workItem);
            Assert.Equal("Hello", workItem.Value.Data);

            if (_assertStats)
            {
                var stats = await queue.GetQueueStatsAsync();
                Assert.Equal(1, stats.Dequeued);
                Assert.Equal(1, stats.Enqueued);
                Assert.Equal(0, stats.Queued);
                Assert.Equal(1, stats.Working);
            }
        }
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    [Fact]
    public async Task CanGetQueueItemWithTimeoutCancelledTokenWithDeliveryDelayMaxWaitOverlap()
    {
        var queue = GetQueue(dequeueInterval: TimeSpan.Zero, readQueueTimeout: TimeSpan.FromSeconds(2));
        if (queue == null)
            return;

        try
        {
            await queue.DeleteQueueAsync();
            // await AssertEmptyQueueAsync(queue); // TODO: Uncomment once foundatio is updated.

            await queue.EnqueueAsync(new SimpleWorkItem { Data = "Hello" }, new QueueEntryOptions { DeliveryDelay = TimeSpan.FromSeconds(3) });
            var workItem = await queue.DequeueAsync(TimeSpan.FromSeconds(3));
            Assert.NotNull(workItem);
            Assert.Equal("Hello", workItem.Value.Data);

            if (_assertStats)
            {
                var stats = await queue.GetQueueStatsAsync();
                Assert.Equal(1, stats.Dequeued);
                Assert.Equal(1, stats.Enqueued);
                Assert.Equal(0, stats.Queued);
                Assert.Equal(1, stats.Working);
            }
        }
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    protected override async Task CleanupQueueAsync(IQueue<SimpleWorkItem> queue)
    {
        await base.CleanupQueueAsync(queue);
        await Task.Delay(TimeSpan.FromSeconds(2));
    }
}
