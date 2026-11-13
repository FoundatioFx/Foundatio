using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amazon.SQS;
using Amazon.SQS.Model;
using Foundatio.Caching;
using Foundatio.Queues;
using Foundatio.Serializer;
using Foundatio.Tests.Queue;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Foundatio.AWS.Tests.Queues;

public class SQSQueueTests : QueueTestBase
{
    private readonly string _queueName = "foundatio-" + Guid.NewGuid().ToString("N").Substring(10);

    public SQSQueueTests(ITestOutputHelper output) : base(output)
    {
        // SQS queue stats are approximate and unreliable
        _assertStats = false;
    }

    protected override IQueue<SimpleWorkItem>? GetQueue(int retries = 1, TimeSpan? workItemTimeout = null,
        TimeSpan? retryDelay = null, int[]? retryMultipliers = null, int deadLetterMaxItems = 100,
        bool runQueueMaintenance = true, TimeProvider? timeProvider = null, ISerializer? serializer = null,
        Func<SimpleWorkItem, string?>? groupIdResolver = null)
    {
        var queue = new SQSQueue<SimpleWorkItem>(o =>
        {
            o.ConnectionString("serviceurl=http://localhost:4566;AccessKey=xxx;SecretKey=xxx")
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
                .MetricsPollingInterval(TimeSpan.Zero)
                .TimeProvider(timeProvider)
                .Serializer(serializer)
                .LoggerFactory(Log);

            if (groupIdResolver is not null)
                o.GroupId(groupIdResolver);

            return o;
        });

        _logger.LogDebug("Queue Id: {QueueId}", queue.QueueId);
        return queue;
    }

    protected IQueue<SimpleWorkItem> GetQueue(int retries = 1, TimeSpan? workItemTimeout = null,
        TimeSpan? retryDelay = null, int[]? retryMultipliers = null, int deadLetterMaxItems = 100,
        bool runQueueMaintenance = true, TimeSpan? dequeueInterval = null, TimeSpan? readQueueTimeout = null)
    {
        var queue = new SQSQueue<SimpleWorkItem>(o => o
            .ConnectionString("serviceurl=http://localhost:4566;AccessKey=xxx;SecretKey=xxx")
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
            .MetricsPollingInterval(TimeSpan.Zero)
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
    public override Task DequeueAsync_AfterAbandonWithMutatedValue_ReturnsOriginalValueAsync()
    {
        return base.DequeueAsync_AfterAbandonWithMutatedValue_ReturnsOriginalValueAsync();
    }

    [Fact]
    public override Task DequeueAsync_WithDispose_AutoAbandonsEntryAsync()
    {
        return base.DequeueAsync_WithDispose_AutoAbandonsEntryAsync();
    }

    [Fact]
    public override Task DequeueWaitWillGetSignaledAsync()
    {
        return base.DequeueWaitWillGetSignaledAsync();
    }

    [Fact]
    public override Task DequeueAsync_WithPoisonMessage_MovesToDeadletterAsync()
    {
        return base.DequeueAsync_WithPoisonMessage_MovesToDeadletterAsync();
    }

    [Fact]
    public override Task DuplicateDetection_WithDifferentIdentifiers_AcceptsBothItemsAsync()
    {
        return base.DuplicateDetection_WithDifferentIdentifiers_AcceptsBothItemsAsync();
    }

    [Fact]
    public override Task DuplicateDetection_WithExpiredWindow_AcceptsDuplicateAsync()
    {
        return base.DuplicateDetection_WithExpiredWindow_AcceptsDuplicateAsync();
    }

    [Fact]
    public override Task DuplicateDetection_WithNullIdentifier_AcceptsAllItemsAsync()
    {
        return base.DuplicateDetection_WithNullIdentifier_AcceptsAllItemsAsync();
    }

    [Fact]
    public override Task EnqueueAsync_WithSerializationError_ThrowsAndLeavesQueueEmptyAsync()
    {
        return base.EnqueueAsync_WithSerializationError_ThrowsAndLeavesQueueEmptyAsync();
    }

    [Fact]
    public override Task EnqueueAsync_WithReusedOptions_DoesNotChangeCallerOptionsAsync()
    {
        return base.EnqueueAsync_WithReusedOptions_DoesNotChangeCallerOptionsAsync();
    }

    [Fact]
    public override Task EnqueueAsync_WhenResolverReturnsEmpty_EnqueuesWithoutGroupAsync()
    {
        return base.EnqueueAsync_WhenResolverReturnsEmpty_EnqueuesWithoutGroupAsync();
    }

    [Fact]
    public override Task EnqueueAsync_WhenResolverReturnsNull_EnqueuesWithoutGroupAsync()
    {
        return base.EnqueueAsync_WhenResolverReturnsNull_EnqueuesWithoutGroupAsync();
    }

    [Fact]
    public override Task EnqueueAsync_WithActivity_AppliesCorrelationBeforeEnqueuingHandlersAsync()
    {
        return base.EnqueueAsync_WithActivity_AppliesCorrelationBeforeEnqueuingHandlersAsync();
    }

    [Fact]
    public override Task EnqueueAsync_WithEmptyGroupIdAndResolver_UsesPayloadValueAsync()
    {
        return base.EnqueueAsync_WithEmptyGroupIdAndResolver_UsesPayloadValueAsync();
    }

    [Fact]
    public override Task EnqueueAsync_WithExplicitGroupId_OverridesResolverAsync()
    {
        return base.EnqueueAsync_WithExplicitGroupId_OverridesResolverAsync();
    }

    [Fact]
    public override Task EnqueueAsync_WithGroupIdResolver_DoesNotChangeCallerOptionsAsync()
    {
        return base.EnqueueAsync_WithGroupIdResolver_DoesNotChangeCallerOptionsAsync();
    }

    [Fact]
    public override Task EnqueueAsync_WithGroupIdResolver_UsesPayloadValueAsync()
    {
        return base.EnqueueAsync_WithGroupIdResolver_UsesPayloadValueAsync();
    }

    [Fact]
    public override Task EnqueueAsync_WithReusedOptionsAndGroupIdResolver_EnqueuesEachMessageAsync()
    {
        return base.EnqueueAsync_WithReusedOptionsAndGroupIdResolver_EnqueuesEachMessageAsync();
    }

    [Fact(Skip = "SQS does not support custom entry IDs")]
    public override Task EnqueueAsync_WithUniqueId_UsesProvidedIdAsync()
    {
        return base.EnqueueAsync_WithUniqueId_UsesProvidedIdAsync();
    }

    [Fact(Skip = "SQS does not support retrieving deadletter items")]
    public override Task GetDeadletterItemsAsync_WithDeadletteredEntry_ReturnsItemsAsync()
    {
        return base.GetDeadletterItemsAsync_WithDeadletteredEntry_ReturnsItemsAsync();
    }

    [Fact]
    public override Task GetQueueActivity_AfterEnqueueAndDequeue_ReturnsTimestampsAsync()
    {
        return base.GetQueueActivity_AfterEnqueueAndDequeue_ReturnsTimestampsAsync();
    }

    [Fact]
    public override Task GetQueueEntryMetadata_AfterDequeue_ReturnsValidTimestampsAsync()
    {
        return base.GetQueueEntryMetadata_AfterDequeue_ReturnsValidTimestampsAsync();
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
    public override Task QueueEntry_EntryType_ReturnsCorrectTypeAsync()
    {
        return base.QueueEntry_EntryType_ReturnsCorrectTypeAsync();
    }

    [Fact]
    public override Task QueueEntry_GetValue_ReturnsUntypedValueAsync()
    {
        return base.QueueEntry_GetValue_ReturnsUntypedValueAsync();
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
        // The SDK can't abort a long poll, so a dequeue can run one poll past its timeout. A poll shorter than half the
        // delivery delay keeps the first dequeue from receiving the delayed message.
        var queue = GetQueue(dequeueInterval: TimeSpan.Zero, readQueueTimeout: TimeSpan.FromSeconds(1));
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

            await Task.Delay(TimeSpan.FromSeconds(3), TestCancellationToken);

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
            Assert.NotNull(workItem?.Value);
            Assert.Equal("Hello", workItem!.Value!.Data);

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
            Assert.NotNull(workItem?.Value);
            Assert.Equal("Hello", workItem!.Value!.Data);

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
    public override Task AbandonAsync_WhenRetriesExceeded_MovesToDeadletterAsync()
    {
        return base.AbandonAsync_WhenRetriesExceeded_MovesToDeadletterAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbandonAsync_WhenRetriesExceededOnExistingQueue_MovesToDeadletterAsync(bool isFifo)
    {
        // Arrange
        string name = isFifo ? GetFifoQueueName() : _queueName;
        using (var creatingQueue = GetNamedQueue(name, retries: 5))
        {
            // A higher retry count gives the queue a redrive maxReceiveCount above this test's retry limit,
            // so the message is dead lettered by SQSQueue rather than by SQS redrive.
            await creatingQueue.EnqueueAsync(new SimpleWorkItem { Data = "dead-letter-test" },
                new QueueEntryOptions { CorrelationId = "correlation-1", GroupId = "tenant-1" });
        }

        var queue = GetNamedQueue(name, retries: 1);

        try
        {
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                var entry = await queue.DequeueAsync(TimeSpan.FromSeconds(5));
                Assert.NotNull(entry);
                await entry.AbandonAsync();
            }

            // Act
            var deadLetteredEntry = await queue.DequeueAsync(TimeSpan.FromSeconds(5));

            // Assert
            Assert.Null(deadLetteredEntry);
            Assert.Null(await queue.DequeueAsync(TimeSpan.FromSeconds(1)));

            string deadLetterName = isFifo ? name[..^".fifo".Length] + "-deadletter.fifo" : name + "-deadletter";
            var deadLetterUrl = await queue.Client.GetQueueUrlAsync(deadLetterName, TestCancellationToken);
            var response = await queue.Client.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = deadLetterUrl.QueueUrl,
                MessageAttributeNames = ["All"],
                MessageSystemAttributeNames = ["All"],
                WaitTimeSeconds = 1
            }, TestCancellationToken);

            var message = Assert.Single(response.Messages);
            Assert.Contains("dead-letter-test", message.Body);
            Assert.Equal("tenant-1", message.Attributes["MessageGroupId"]);
            Assert.Equal("correlation-1", message.MessageAttributes["CorrelationId"].StringValue);
        }
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    [Fact]
    public async Task AbandonAsync_WhenRetriesExceededWithoutRedrivePolicy_DeletesMessageAsync()
    {
        // Arrange
        using (var creatingQueue = GetNamedQueue(_queueName, supportDeadLetter: false))
            await creatingQueue.EnqueueAsync(new SimpleWorkItem { Data = "no-redrive" });

        var queue = GetNamedQueue(_queueName, retries: 1);

        try
        {
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                var entry = await queue.DequeueAsync(TimeSpan.FromSeconds(5));
                Assert.NotNull(entry);
                await entry.AbandonAsync();
            }

            // Act
            var deadLetteredEntry = await queue.DequeueAsync(TimeSpan.FromSeconds(5));

            // Assert
            Assert.Null(deadLetteredEntry);
            Assert.Null(await queue.DequeueAsync(TimeSpan.FromSeconds(1)));
            await Assert.ThrowsAsync<QueueDoesNotExistException>(() => queue.Client.GetQueueUrlAsync(_queueName + "-deadletter", TestCancellationToken));
        }
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    [Fact]
    public override Task AbandonAsync_WithGroupId_PreservesGroupIdOnRetryAsync()
    {
        return base.AbandonAsync_WithGroupId_PreservesGroupIdOnRetryAsync();
    }

    [Fact]
    public override Task AbandonAsync_WithGroupIdAndRetryDelay_PreservesGroupIdOnRetryAsync()
    {
        return base.AbandonAsync_WithGroupIdAndRetryDelay_PreservesGroupIdOnRetryAsync();
    }

    [Fact]
    public async Task DeleteQueueAsync_WithExternalDeadletterQueue_KeepsDeadletterQueueAsync()
    {
        // Arrange
        string externalDeadLetterName = _queueName + "-external-dlq";
        var queue = GetNamedQueue(_queueName);
        var deadLetter = await queue.Client.CreateQueueAsync(externalDeadLetterName, TestCancellationToken);

        try
        {
            var deadLetterAttributes = await queue.Client.GetQueueAttributesAsync(deadLetter.QueueUrl, [QueueAttributeName.QueueArn], TestCancellationToken);
            await queue.Client.CreateQueueAsync(new CreateQueueRequest
            {
                QueueName = _queueName,
                Attributes = new Dictionary<string, string>
                {
                    [QueueAttributeName.RedrivePolicy] = $$"""{"deadLetterTargetArn":"{{deadLetterAttributes.QueueARN}}","maxReceiveCount":"10"}"""
                }
            }, TestCancellationToken);

            await queue.EnqueueAsync(new SimpleWorkItem { Data = "test" });
            await queue.GetQueueStatsAsync();

            // Act
            await queue.DeleteQueueAsync();

            // Assert
            await Assert.ThrowsAsync<QueueDoesNotExistException>(() => queue.Client.GetQueueUrlAsync(_queueName, TestCancellationToken));
            var externalUrl = await queue.Client.GetQueueUrlAsync(externalDeadLetterName, TestCancellationToken);
            Assert.Equal(deadLetter.QueueUrl, externalUrl.QueueUrl);
        }
        finally
        {
            await queue.Client.DeleteQueueAsync(deadLetter.QueueUrl, TestCancellationToken);
            queue.Dispose();
        }
    }

    [Fact]
    public async Task DequeueAsync_WithSameGroupOnFifoQueue_ReturnsMessagesInOrderAsync()
    {
        // Arrange
        var queue = GetNamedQueue(GetFifoQueueName());
        string?[] expected = ["first", "second", "third"];

        try
        {
            foreach (string? data in expected)
                await queue.EnqueueAsync(new SimpleWorkItem { Data = data }, new QueueEntryOptions { GroupId = "tenant-1" });

            // Act
            var actual = new List<string?>();
            for (int i = 0; i < expected.Length; i++)
            {
                var entry = await queue.DequeueAsync(TimeSpan.FromSeconds(5));
                Assert.NotNull(entry);
                Assert.Equal("tenant-1", entry.GroupId);
                actual.Add(entry.Value.Data);
                await entry.CompleteAsync();
            }

            // Assert
            Assert.Equal(expected, actual);
        }
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    [Fact]
    public async Task EnqueueAsync_WithDeliveryDelayOnFifoQueue_ThrowsQueueExceptionAsync()
    {
        // Arrange
        var queue = GetNamedQueue(GetFifoQueueName());
        var options = new QueueEntryOptions { GroupId = "tenant-1", DeliveryDelay = TimeSpan.FromSeconds(5) };

        try
        {
            // Act & Assert
            await Assert.ThrowsAsync<QueueException>(() => queue.EnqueueAsync(new SimpleWorkItem { Data = "test" }, options));
        }
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    [Fact]
    public async Task EnqueueAsync_WithZeroDeliveryDelayOnFifoQueue_EnqueuesAsync()
    {
        // Arrange
        var queue = GetNamedQueue(GetFifoQueueName());
        var options = new QueueEntryOptions { GroupId = "tenant-1", DeliveryDelay = TimeSpan.Zero };

        try
        {
            // Act
            string? id = await queue.EnqueueAsync(new SimpleWorkItem { Data = "test" }, options);

            // Assert
            Assert.NotNull(id);
            var entry = await queue.DequeueAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(entry);
            Assert.Equal("test", entry.Value.Data);
            await entry.CompleteAsync();
        }
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    [Fact]
    public async Task EnqueueAsync_WithDuplicateUniqueIdOnFifoQueue_DeliversOnceAsync()
    {
        // Arrange
        var queue = GetNamedQueue(GetFifoQueueName());

        try
        {
            // Act
            await queue.EnqueueAsync(new SimpleWorkItem { Data = "first" }, new QueueEntryOptions { GroupId = "tenant-1", UniqueId = "order-1" });
            await queue.EnqueueAsync(new SimpleWorkItem { Data = "duplicate" }, new QueueEntryOptions { GroupId = "tenant-1", UniqueId = "order-1" });

            // Assert
            var entry = await queue.DequeueAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(entry);
            Assert.Equal("first", entry.Value.Data);
            await entry.CompleteAsync();

            Assert.Null(await queue.DequeueAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    [Fact]
    public override Task EnqueueAsync_WhenEnqueuingHandlerClearsGroupId_EnqueuesWithoutGroupAsync()
    {
        return base.EnqueueAsync_WhenEnqueuingHandlerClearsGroupId_EnqueuesWithoutGroupAsync();
    }

    [Fact]
    public override Task EnqueueAsync_WithEmptyGroupId_EnqueuesWithoutGroupAsync()
    {
        return base.EnqueueAsync_WithEmptyGroupId_EnqueuesWithoutGroupAsync();
    }

    [Fact]
    public async Task EnqueueAsync_WithFifoQueueName_CreatesFifoDeadletterQueueAsync()
    {
        // Arrange
        string name = GetFifoQueueName();
        string expectedDeadLetterName = name[..^".fifo".Length] + "-deadletter.fifo";
        var queue = GetNamedQueue(name);

        try
        {
            // Act
            await queue.EnqueueAsync(new SimpleWorkItem { Data = "test" }, new QueueEntryOptions { GroupId = "tenant-1" });

            // Assert
            var queueUrl = await queue.Client.GetQueueUrlAsync(name, TestCancellationToken);
            var queueAttributes = await queue.Client.GetQueueAttributesAsync(queueUrl.QueueUrl, [QueueAttributeName.FifoQueue, QueueAttributeName.RedrivePolicy], TestCancellationToken);
            Assert.Equal("true", queueAttributes.Attributes[QueueAttributeName.FifoQueue]);
            Assert.Equal(expectedDeadLetterName, queueAttributes.Attributes.DeadLetterQueue());

            var deadLetterUrl = await queue.Client.GetQueueUrlAsync(expectedDeadLetterName, TestCancellationToken);
            var deadLetterAttributes = await queue.Client.GetQueueAttributesAsync(deadLetterUrl.QueueUrl, [QueueAttributeName.FifoQueue], TestCancellationToken);
            Assert.Equal("true", deadLetterAttributes.Attributes[QueueAttributeName.FifoQueue]);
        }
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    [Fact]
    public override Task EnqueueAsync_WithGroupId_RoundTripsGroupIdAsync()
    {
        return base.EnqueueAsync_WithGroupId_RoundTripsGroupIdAsync();
    }

    [Theory]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public async Task EnqueueAsync_WithGroupIdLength_EnforcesSqsMaximumAsync(int length, bool isValid)
    {
        // Arrange
        var queue = GetNamedQueue(_queueName);
        var options = new QueueEntryOptions { GroupId = new string('a', length) };

        try
        {
            // Act
            var exception = await Record.ExceptionAsync(async () => await queue.EnqueueAsync(new SimpleWorkItem { Data = "test" }, options));

            // Assert
            if (isValid)
                Assert.Null(exception);
            else
                Assert.IsType<ArgumentException>(exception);
        }
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    [Fact]
    public async Task EnqueueAsync_WithGroupIdOnStandardQueue_SendsMessageGroupIdAsync()
    {
        // Arrange
        var queue = GetNamedQueue(_queueName);

        try
        {
            // Act
            await queue.EnqueueAsync(new SimpleWorkItem { Data = "group-id-test" }, new QueueEntryOptions { GroupId = "tenant-123" });

            // Assert
            var entry = await queue.DequeueAsync(TimeSpan.FromSeconds(5));
            var sqsEntry = Assert.IsType<SQSQueueEntry<SimpleWorkItem>>(entry);
            Assert.Equal("tenant-123", sqsEntry.UnderlyingMessage.Attributes["MessageGroupId"]);
            Assert.Equal("tenant-123", sqsEntry.GroupId);
            Assert.Empty(sqsEntry.Properties);
            await sqsEntry.CompleteAsync();
        }
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    [Theory]
    [InlineData("tenant 1")]
    [InlineData("tenant\u00e9")]
    [InlineData("tenant\t1")]
    public async Task EnqueueAsync_WithInvalidGroupId_ThrowsBeforeEnqueuingAsync(string groupId)
    {
        // Arrange
        var queue = GetNamedQueue(_queueName);
        int enqueuingCount = 0;
        using var _ = queue.Enqueuing.AddSyncHandler((_, _) => enqueuingCount++);

        try
        {
            // Act & Assert
            await Assert.ThrowsAsync<ArgumentException>(() => queue.EnqueueAsync(new SimpleWorkItem { Data = "test" }, new QueueEntryOptions { GroupId = groupId }));
            Assert.Equal(0, enqueuingCount);
        }
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    [Theory]
    [InlineData("order 1")]
    [InlineData("order\u00e9")]
    public async Task EnqueueAsync_WithInvalidUniqueIdOnFifoQueue_ThrowsArgumentExceptionAsync(string uniqueId)
    {
        // Arrange
        var queue = GetNamedQueue(GetFifoQueueName());
        var options = new QueueEntryOptions { GroupId = "tenant-1", UniqueId = uniqueId };

        try
        {
            // Act & Assert
            await Assert.ThrowsAsync<ArgumentException>(() => queue.EnqueueAsync(new SimpleWorkItem { Data = "test" }, options));
        }
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    [Fact]
    public async Task EnqueueAsync_WithMoreThanTenAttributes_ThrowsQueueExceptionAsync()
    {
        // Arrange
        var queue = GetNamedQueue(_queueName);
        int enqueuingCount = 0;
        using var _ = queue.Enqueuing.AddSyncHandler((_, _) => enqueuingCount++);
        var options = new QueueEntryOptions { CorrelationId = "correlation-id" };
        for (int i = 0; i < 10; i++)
            options.Properties["property" + i] = "value" + i;

        try
        {
            // Act & Assert
            await Assert.ThrowsAsync<QueueException>(() => queue.EnqueueAsync(new SimpleWorkItem { Data = "test" }, options));
            Assert.Equal(0, enqueuingCount);
        }
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    [Fact]
    public async Task EnqueueAsync_WhenEnqueuingHandlerClearsGroupIdOnFifoQueue_ThrowsQueueExceptionAsync()
    {
        // Arrange
        var queue = GetNamedQueue(GetFifoQueueName());
        using var _ = queue.Enqueuing.AddSyncHandler((_, args) => args.Options.GroupId = null);

        try
        {
            // Act & Assert
            await Assert.ThrowsAsync<QueueException>(() => queue.EnqueueAsync(new SimpleWorkItem { Data = "test" }, new QueueEntryOptions { GroupId = "tenant-1" }));
        }
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    [Fact]
    public async Task EnqueueAsync_WhenEnqueuingHandlerSetsInvalidGroupId_ThrowsArgumentExceptionAsync()
    {
        // Arrange
        var queue = GetNamedQueue(_queueName);
        using var _ = queue.Enqueuing.AddSyncHandler((_, args) => args.Options.GroupId = "invalid group");

        try
        {
            // Act & Assert
            await Assert.ThrowsAsync<ArgumentException>(() => queue.EnqueueAsync(new SimpleWorkItem { Data = "test" }));
        }
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    [Fact]
    public async Task EnqueueAsync_WithoutGroupIdOnFifoQueue_ThrowsQueueExceptionAsync()
    {
        // Arrange
        var queue = GetNamedQueue(GetFifoQueueName());

        try
        {
            // Act & Assert
            await Assert.ThrowsAsync<QueueException>(() => queue.EnqueueAsync(new SimpleWorkItem { Data = "test" }));
        }
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    [Fact]
    public async Task EnqueueAsync_WithoutUniqueIdOnFifoQueue_DeliversIdenticalMessagesAsync()
    {
        // Arrange
        var queue = GetNamedQueue(GetFifoQueueName());

        try
        {
            // Act
            await queue.EnqueueAsync(new SimpleWorkItem { Data = "same" }, new QueueEntryOptions { GroupId = "tenant-1" });
            await queue.EnqueueAsync(new SimpleWorkItem { Data = "same" }, new QueueEntryOptions { GroupId = "tenant-1" });

            // Assert
            for (int i = 0; i < 2; i++)
            {
                var entry = await queue.DequeueAsync(TimeSpan.FromSeconds(5));
                Assert.NotNull(entry);
                Assert.Equal("same", entry.Value.Data);
                await entry.CompleteAsync();
            }
        }
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    [Fact]
    public async Task EnqueueAsync_WithPropertyNamedCorrelationId_UsesCorrelationIdOptionAsync()
    {
        // Arrange
        var queue = GetNamedQueue(_queueName);
        var options = new QueueEntryOptions { CorrelationId = "real-correlation-id" };
        options.Properties["CorrelationId"] = "property-value";

        try
        {
            // Act
            string? id = await queue.EnqueueAsync(new SimpleWorkItem { Data = "test" }, options);

            // Assert
            Assert.False(String.IsNullOrEmpty(id));
            var entry = await queue.DequeueAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(entry);
            Assert.Equal("real-correlation-id", entry.CorrelationId);
            await entry.CompleteAsync();
        }
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    [Fact]
    public async Task EnqueueAsync_WithUniqueIdOnStandardQueue_DoesNotSendDeduplicationIdAsync()
    {
        // Arrange
        var queue = GetNamedQueue(_queueName);

        try
        {
            // Act
            string? id = await queue.EnqueueAsync(new SimpleWorkItem { Data = "unique-id-test" }, new QueueEntryOptions { UniqueId = "my-unique-id" });

            // Assert
            Assert.False(String.IsNullOrEmpty(id));
            var entry = await queue.DequeueAsync(TimeSpan.FromSeconds(5));
            var sqsEntry = Assert.IsType<SQSQueueEntry<SimpleWorkItem>>(entry);
            Assert.Equal("unique-id-test", sqsEntry.Value.Data);
            Assert.False(sqsEntry.UnderlyingMessage.Attributes.ContainsKey("MessageDeduplicationId"));
            await sqsEntry.CompleteAsync();
        }
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    [Fact]
    public async Task AbandonAsync_WhenDeadletterQueueIsMissing_RetainsMessageAsync()
    {
        // Arrange
        string deadLetterName = _queueName + "-missing";
        using (var creatingQueue = GetNamedQueue(_queueName, supportDeadLetter: false))
            await creatingQueue.EnqueueAsync(new SimpleWorkItem { Data = "retained" });

        var queue = GetNamedQueue(_queueName, retries: 0);
        string? deadLetterUrl = null;

        try
        {
            await SetRedrivePolicyAsync(queue, _queueName, deadLetterName);
            var entry = await queue.DequeueAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(entry);

            // Act
            await Assert.ThrowsAnyAsync<AmazonSQSException>(() => entry.AbandonAsync());

            // Assert
            Assert.False(entry.IsAbandoned);
            deadLetterUrl = (await queue.Client.CreateQueueAsync(deadLetterName, TestCancellationToken)).QueueUrl;
            await entry.AbandonAsync();
            Assert.True(entry.IsAbandoned);
            Assert.Contains("retained", await ReceiveBodyAsync(queue, deadLetterUrl));
        }
        finally
        {
            if (deadLetterUrl is not null)
                await queue.Client.DeleteQueueAsync(deadLetterUrl, TestCancellationToken);

            await CleanupQueueAsync(queue);
        }
    }

    [Fact]
    public async Task AbandonAsync_WhenRedrivePolicyIsAddedOrChanged_UsesCurrentDeadletterQueueAsync()
    {
        // Arrange
        using (var creatingQueue = GetNamedQueue(_queueName, supportDeadLetter: false))
        {
            for (int i = 0; i < 3; i++)
                await creatingQueue.EnqueueAsync(new SimpleWorkItem { Data = "item-" + i });
        }

        var queue = GetNamedQueue(_queueName, retries: 0);
        string firstDeadLetterUrl = (await queue.Client.CreateQueueAsync(_queueName + "-first", TestCancellationToken)).QueueUrl;
        string secondDeadLetterUrl = (await queue.Client.CreateQueueAsync(_queueName + "-second", TestCancellationToken)).QueueUrl;

        try
        {
            var withoutPolicy = await queue.DequeueAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(withoutPolicy);
            await withoutPolicy.AbandonAsync();

            await SetRedrivePolicyAsync(queue, _queueName, _queueName + "-first");
            var afterAdd = await queue.DequeueAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(afterAdd);
            await afterAdd.AbandonAsync();

            await SetRedrivePolicyAsync(queue, _queueName, _queueName + "-second");
            var afterChange = await queue.DequeueAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(afterChange);

            // Act
            await afterChange.AbandonAsync();

            // Assert
            Assert.Equal(afterAdd.Value.Data, Deserialize(await ReceiveBodyAsync(queue, firstDeadLetterUrl)));
            Assert.Equal(afterChange.Value.Data, Deserialize(await ReceiveBodyAsync(queue, secondDeadLetterUrl)));
            Assert.Null(await queue.DequeueAsync(TimeSpan.FromSeconds(1)));
        }
        finally
        {
            await queue.Client.DeleteQueueAsync(firstDeadLetterUrl, TestCancellationToken);
            await queue.Client.DeleteQueueAsync(secondDeadLetterUrl, TestCancellationToken);
            await CleanupQueueAsync(queue);
        }
    }

    [Fact]
    public async Task StartWorkingAsync_WhenDeadletterQueueIsMissing_KeepsWorkingAndRetriesAsync()
    {
        // Arrange
        string deadLetterName = _queueName + "-missing";
        using (var creatingQueue = GetNamedQueue(_queueName, supportDeadLetter: false))
            await creatingQueue.EnqueueAsync(new SimpleWorkItem { Data = "retried" });

        var queue = GetNamedQueue(_queueName, retries: 0, workItemTimeout: TimeSpan.FromSeconds(1));
        string? deadLetterUrl = null;
        using var workerCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestCancellationToken);

        try
        {
            await SetRedrivePolicyAsync(queue, _queueName, deadLetterName);

            // Act
            await queue.StartWorkingAsync((_, _) => throw new InvalidOperationException("Handler failure"), cancellationToken: workerCancellation.Token);
            for (int i = 0; i < 20 && !Log.LogEntries.Any(e => e.Message.Contains("Worker error abandoning queue entry")); i++)
                await Task.Delay(TimeSpan.FromMilliseconds(250), TestCancellationToken);

            deadLetterUrl = (await queue.Client.CreateQueueAsync(deadLetterName, TestCancellationToken)).QueueUrl;

            // Assert
            string? body = null;
            for (int i = 0; i < 5 && body is null; i++)
                body = await ReceiveBodyAsync(queue, deadLetterUrl);

            Assert.Contains(Log.LogEntries, e => e.Message.Contains("Worker error abandoning queue entry"));
            Assert.Contains("retried", body);
        }
        finally
        {
            await workerCancellation.CancelAsync();
            if (deadLetterUrl is not null)
                await queue.Client.DeleteQueueAsync(deadLetterUrl, TestCancellationToken);

            await CleanupQueueAsync(queue);
        }
    }

    [Theory]
    [InlineData(10, false)]
    [InlineData(9, true)]
    public async Task EnqueueAsync_WithActivityAttributeOverflow_DoesNotReserveDuplicateKeyAsync(int propertyCount, bool traceState)
    {
        // Arrange
        using var activity = new Activity("enqueue").Start();
        activity.TraceStateString = traceState ? "vendor=value" : null;
        using var cache = new InMemoryCacheClient();
        var queue = GetNamedQueue(_queueName);
        queue.AttachBehavior(new DuplicateDetectionQueueBehavior<SimpleWorkItem>(cache, Log));
        var options = new QueueEntryOptions();
        for (int i = 0; i < propertyCount; i++)
            options.Properties.Add("property" + i, "value");

        try
        {
            // Act
            await Assert.ThrowsAsync<QueueException>(() => queue.EnqueueAsync(new SimpleWorkItem { UniqueIdentifier = "work-item" }, options));

            // Assert
            Assert.False(await cache.ExistsAsync("work-item"));
            Assert.Null(await queue.DequeueAsync(TimeSpan.FromSeconds(1)));
        }
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    [Fact]
    public async Task EnqueueAsync_WithCaseInsensitivePropertyNames_DoesNotReserveDuplicateKeyAsync()
    {
        // Arrange
        using var cache = new InMemoryCacheClient();
        var queue = GetNamedQueue(_queueName);
        queue.AttachBehavior(new DuplicateDetectionQueueBehavior<SimpleWorkItem>(cache, Log));
        var options = new QueueEntryOptions
        {
            CorrelationId = "explicit",
            Properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["correlationid"] = "property" }
        };
        for (int i = 0; i < 9; i++)
            options.Properties.Add("property" + i, "value");

        try
        {
            // Act
            await Assert.ThrowsAsync<QueueException>(() => queue.EnqueueAsync(new SimpleWorkItem { UniqueIdentifier = "work-item" }, options));

            // Assert
            Assert.False(await cache.ExistsAsync("work-item"));
            Assert.Null(await queue.DequeueAsync(TimeSpan.FromSeconds(1)));
        }
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnqueueAsync_WithMetadataNameCollisions_CountsEffectiveAttributesAsync(bool explicitCorrelation)
    {
        // Arrange
        using var activity = new Activity("enqueue").Start();
        activity.TraceStateString = "vendor=value";
        var queue = GetNamedQueue(_queueName);
        var options = new QueueEntryOptions { CorrelationId = explicitCorrelation ? "explicit" : null };
        options.Properties.Add("CorrelationId", "property-correlation");
        options.Properties.Add("TraceState", "property-trace");
        for (int i = 0; i < 8; i++)
            options.Properties.Add("property" + i, "value");
        bool enqueuingCalled = false;
        queue.Enqueuing.AddHandler((_, args) =>
        {
            enqueuingCalled = true;
            args.Cancel = true;
            return Task.CompletedTask;
        });

        try
        {
            // Act
            string? id = await queue.EnqueueAsync(new SimpleWorkItem(), options);

            // Assert
            Assert.Null(id);
            Assert.True(enqueuingCalled);
        }
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnqueueAsync_WhenDeadletterNameExceedsSqsLimit_ThrowsBeforeCreatingQueueAsync(bool isFifo)
    {
        // Arrange
        string suffix = isFifo ? ".fifo" : String.Empty;
        string name = "foundatio-" + new string('a', 80 - "foundatio-".Length - suffix.Length) + suffix;
        var queue = GetNamedQueue(name);

        try
        {
            // Act
            var exception = await Assert.ThrowsAsync<QueueException>(() => queue.EnqueueAsync(new SimpleWorkItem(), new QueueEntryOptions { GroupId = "group" }));

            // Assert
            Assert.Contains("exceeds the SQS limit of 80 characters", exception.Message);
            await Assert.ThrowsAsync<QueueDoesNotExistException>(() => queue.Client.GetQueueUrlAsync(name, TestCancellationToken));
        }
        finally
        {
            queue.Dispose();
        }
    }

    [Fact]
    public async Task AbandonAsync_WhenCreatedRedrivePolicyIsNotVisibleYet_UsesCreatedDeadletterQueueAsync()
    {
        // Arrange
        var queue = GetNamedQueue(_queueName, retries: 0);

        try
        {
            await queue.EnqueueAsync(new SimpleWorkItem { Data = "propagating" });
            var queueUrl = await queue.Client.GetQueueUrlAsync(_queueName, TestCancellationToken);
            await queue.Client.SetQueueAttributesAsync(queueUrl.QueueUrl, new Dictionary<string, string> { [QueueAttributeName.RedrivePolicy] = "" }, TestCancellationToken);
            var entry = await queue.DequeueAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(entry);

            // Act
            await entry.AbandonAsync();

            // Assert
            var deadLetterUrl = await queue.Client.GetQueueUrlAsync(_queueName + "-deadletter", TestCancellationToken);
            Assert.Equal("propagating", Deserialize(await ReceiveBodyAsync(queue, deadLetterUrl.QueueUrl)));
        }
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    [Fact]
    public async Task DeleteQueueAsync_OnUnusedInstance_DeletesSourceAndDeadletterQueuesAsync()
    {
        // Arrange
        using (var creatingQueue = GetNamedQueue(_queueName))
            await creatingQueue.EnqueueAsync(new SimpleWorkItem { Data = "delete" });

        var queue = GetNamedQueue(_queueName);

        try
        {
            // Act
            await queue.DeleteQueueAsync();

            // Assert
            await Assert.ThrowsAsync<QueueDoesNotExistException>(() => queue.Client.GetQueueUrlAsync(_queueName, TestCancellationToken));
            await Assert.ThrowsAsync<QueueDoesNotExistException>(() => queue.Client.GetQueueUrlAsync(_queueName + "-deadletter", TestCancellationToken));
        }
        finally
        {
            queue.Dispose();
        }
    }

    [Fact]
    public async Task DeleteQueueAsync_AfterRedrivePolicyRemoved_KeepsResolvedDeadletterQueueAsync()
    {
        // Arrange
        string deadLetterName = _queueName + "-deadletter";
        var queue = GetNamedQueue(_queueName);
        var deadLetter = await queue.Client.CreateQueueAsync(deadLetterName, TestCancellationToken);
        var source = await queue.Client.CreateQueueAsync(_queueName, TestCancellationToken);

        try
        {
            // The instance opens the existing queue and resolves the conventional dead letter queue from its policy.
            await SetRedrivePolicyAsync(queue, _queueName, deadLetterName);
            await queue.EnqueueAsync(new SimpleWorkItem { Data = "test" });
            var stats = await queue.GetQueueStatsAsync();
            Assert.Equal(0, stats.Deadletter);
            await queue.Client.SetQueueAttributesAsync(source.QueueUrl, new Dictionary<string, string> { [QueueAttributeName.RedrivePolicy] = "" }, TestCancellationToken);

            // Act
            await queue.DeleteQueueAsync();

            // Assert
            await Assert.ThrowsAsync<QueueDoesNotExistException>(() => queue.Client.GetQueueUrlAsync(_queueName, TestCancellationToken));
            var deadLetterUrl = await queue.Client.GetQueueUrlAsync(deadLetterName, TestCancellationToken);
            Assert.Equal(deadLetter.QueueUrl, deadLetterUrl.QueueUrl);
        }
        finally
        {
            await queue.Client.DeleteQueueAsync(deadLetter.QueueUrl, TestCancellationToken);
            queue.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteQueueAsync_OnUnusedInstanceWithUnrelatedConventionalDeadletterQueue_KeepsItAsync(bool sourceExists)
    {
        // Arrange
        var queue = GetNamedQueue(_queueName);
        var unrelated = await queue.Client.CreateQueueAsync(_queueName + "-deadletter", TestCancellationToken);
        if (sourceExists)
            await queue.Client.CreateQueueAsync(_queueName, TestCancellationToken);

        try
        {
            // Act
            await queue.DeleteQueueAsync();

            // Assert
            await Assert.ThrowsAsync<QueueDoesNotExistException>(() => queue.Client.GetQueueUrlAsync(_queueName, TestCancellationToken));
            var unrelatedUrl = await queue.Client.GetQueueUrlAsync(_queueName + "-deadletter", TestCancellationToken);
            Assert.Equal(unrelated.QueueUrl, unrelatedUrl.QueueUrl);
        }
        finally
        {
            await queue.Client.DeleteQueueAsync(unrelated.QueueUrl, TestCancellationToken);
            queue.Dispose();
        }
    }

    [Fact]
    public async Task EnqueueAsync_OnFifoQueueWithContentBasedDeduplication_DeduplicatesIdenticalBodiesAsync()
    {
        // Arrange
        string fifoName = GetFifoQueueName();
        using (var creatingQueue = GetNamedQueue(fifoName, supportDeadLetter: false))
            await creatingQueue.Client.CreateQueueAsync(new CreateQueueRequest
            {
                QueueName = fifoName,
                Attributes = new Dictionary<string, string>
                {
                    [QueueAttributeName.FifoQueue] = "true",
                    [QueueAttributeName.ContentBasedDeduplication] = "true"
                }
            }, TestCancellationToken);

        var queue = GetNamedQueue(fifoName, supportDeadLetter: false);

        try
        {
            // Act
            await queue.EnqueueAsync(new SimpleWorkItem { Data = "same" }, new QueueEntryOptions { GroupId = "group" });
            await queue.EnqueueAsync(new SimpleWorkItem { Data = "same" }, new QueueEntryOptions { GroupId = "group" });

            // Assert
            var first = await queue.DequeueAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(first);
            await first.CompleteAsync();
            Assert.Null(await queue.DequeueAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    [Fact]
    public async Task EnqueueAsync_WhenContentBasedDeduplicationIsTurnedOff_SendsGeneratedDeduplicationIdAsync()
    {
        // Arrange
        string fifoName = GetFifoQueueName();
        using (var creatingQueue = GetNamedQueue(fifoName, supportDeadLetter: false))
            await creatingQueue.Client.CreateQueueAsync(new CreateQueueRequest
            {
                QueueName = fifoName,
                Attributes = new Dictionary<string, string>
                {
                    [QueueAttributeName.FifoQueue] = "true",
                    [QueueAttributeName.ContentBasedDeduplication] = "true"
                }
            }, TestCancellationToken);

        var queue = GetNamedQueue(fifoName, supportDeadLetter: false);

        try
        {
            await queue.EnqueueAsync(new SimpleWorkItem { Data = "first" }, new QueueEntryOptions { GroupId = "group" });
            var queueUrl = await queue.Client.GetQueueUrlAsync(fifoName, TestCancellationToken);
            await queue.Client.SetQueueAttributesAsync(queueUrl.QueueUrl, new Dictionary<string, string> { [QueueAttributeName.ContentBasedDeduplication] = "false" }, TestCancellationToken);

            // Act
            await queue.EnqueueAsync(new SimpleWorkItem { Data = "second" }, new QueueEntryOptions { GroupId = "group" });

            // Assert
            var first = await queue.DequeueAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(first);
            await first.CompleteAsync();
            var second = await queue.DequeueAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(second);
            Assert.Equal("second", second.Value.Data);
            await second.CompleteAsync();
        }
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnqueueAsync_AfterQueueDeletedExternally_RecreatesQueueOnNextCallAsync(bool detectedByStats)
    {
        // Arrange
        var queue = GetNamedQueue(_queueName, supportDeadLetter: false);

        try
        {
            await queue.EnqueueAsync(new SimpleWorkItem { Data = "before" });
            var queueUrl = await queue.Client.GetQueueUrlAsync(_queueName, TestCancellationToken);
            await queue.Client.DeleteQueueAsync(queueUrl.QueueUrl, TestCancellationToken);

            // Act
            if (detectedByStats)
                await Assert.ThrowsAsync<QueueDoesNotExistException>(() => queue.GetQueueStatsAsync());
            else
                await Assert.ThrowsAsync<QueueDoesNotExistException>(() => queue.EnqueueAsync(new SimpleWorkItem { Data = "stale" }));

            await queue.EnqueueAsync(new SimpleWorkItem { Data = "after" });

            // Assert
            var entry = await queue.DequeueAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(entry);
            Assert.Equal("after", entry.Value.Data);
            await entry.CompleteAsync();
        }
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    [Fact]
    public async Task EnqueueAsync_WhenContentBasedDeduplicationIsTurnedOnAfterQueueCreated_DeduplicatesIdenticalBodiesAsync()
    {
        // Arrange
        string fifoName = GetFifoQueueName();
        var queue = GetNamedQueue(fifoName, supportDeadLetter: false);

        try
        {
            Assert.Null(await queue.DequeueAsync(TimeSpan.Zero));
            var queueUrl = await queue.Client.GetQueueUrlAsync(fifoName, TestCancellationToken);
            await queue.Client.SetQueueAttributesAsync(queueUrl.QueueUrl, new Dictionary<string, string> { [QueueAttributeName.ContentBasedDeduplication] = "true" }, TestCancellationToken);

            // Act
            await queue.EnqueueAsync(new SimpleWorkItem { Data = "same" }, new QueueEntryOptions { GroupId = "group" });
            await queue.EnqueueAsync(new SimpleWorkItem { Data = "same" }, new QueueEntryOptions { GroupId = "group" });

            // Assert
            var first = await queue.DequeueAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(first);
            await first.CompleteAsync();
            Assert.Null(await queue.DequeueAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    [Fact]
    public async Task EnqueueAsync_WhenFifoQueueDeletedExternallyBeforeDeduplicationIsRead_RecreatesQueueOnNextCallAsync()
    {
        // Arrange
        string fifoName = GetFifoQueueName();
        var queue = GetNamedQueue(fifoName, supportDeadLetter: false);

        try
        {
            await queue.EnqueueAsync(new SimpleWorkItem { Data = "before" }, new QueueEntryOptions { GroupId = "group", UniqueId = "before" });
            var queueUrl = await queue.Client.GetQueueUrlAsync(fifoName, TestCancellationToken);
            await queue.Client.DeleteQueueAsync(queueUrl.QueueUrl, TestCancellationToken);

            // Act
            await Assert.ThrowsAsync<QueueDoesNotExistException>(() => queue.EnqueueAsync(new SimpleWorkItem { Data = "stale" }, new QueueEntryOptions { GroupId = "group" }));
            await queue.EnqueueAsync(new SimpleWorkItem { Data = "after" }, new QueueEntryOptions { GroupId = "group" });

            // Assert
            var entry = await queue.DequeueAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(entry);
            Assert.Equal("after", entry.Value.Data);
            await entry.CompleteAsync();
        }
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    private static async Task SetRedrivePolicyAsync(SQSQueue<SimpleWorkItem> queue, string queueName, string deadLetterName)
    {
        var queueUrl = await queue.Client.GetQueueUrlAsync(queueName);
        string redrivePolicy = $$"""{"deadLetterTargetArn":"arn:aws:sqs:us-east-1:000000000000:{{deadLetterName}}","maxReceiveCount":"10"}""";
        await queue.Client.SetQueueAttributesAsync(queueUrl.QueueUrl, new Dictionary<string, string> { [QueueAttributeName.RedrivePolicy] = redrivePolicy });
    }

    private async Task<string?> ReceiveBodyAsync(SQSQueue<SimpleWorkItem> queue, string queueUrl)
    {
        var response = await queue.Client.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, WaitTimeSeconds = 2 }, TestCancellationToken);
        return response.Messages?.SingleOrDefault()?.Body;
    }

    private static string? Deserialize(string? body) =>
        body is null ? null : System.Text.Json.JsonSerializer.Deserialize<SimpleWorkItem>(body)?.Data;

    private static string GetFifoQueueName() => "foundatio-" + Guid.NewGuid().ToString("N").Substring(10) + ".fifo";

    private SQSQueue<SimpleWorkItem> GetNamedQueue(string name, int retries = 1, bool supportDeadLetter = true, TimeSpan? workItemTimeout = null)
    {
        var queue = new SQSQueue<SimpleWorkItem>(o => o
            .ConnectionString("serviceurl=http://localhost:4566;AccessKey=xxx;SecretKey=xxx")
            .Name(name)
            .Retries(retries)
            .SupportDeadLetter(supportDeadLetter)
            .RetryDelay(_ => TimeSpan.Zero)
            .WorkItemTimeout(workItemTimeout ?? TimeSpan.FromMinutes(5))
            .DequeueInterval(TimeSpan.FromSeconds(1))
            .ReadQueueTimeout(TimeSpan.FromSeconds(1))
            .MetricsPollingInterval(TimeSpan.Zero)
            .LoggerFactory(Log));

        _logger.LogDebug("Queue Id: {QueueId}", queue.QueueId);
        return queue;
    }

    protected override async Task CleanupQueueAsync(IQueue<SimpleWorkItem> queue)
    {
        await base.CleanupQueueAsync(queue);
        await Task.Delay(TimeSpan.FromSeconds(2));
    }
}
