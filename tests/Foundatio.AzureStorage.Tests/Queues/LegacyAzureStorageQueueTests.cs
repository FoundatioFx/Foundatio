using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Foundatio.Queues;
using Foundatio.Serializer;
using Foundatio.Tests.Queue;
using Foundatio.Tests.Utility;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Foundatio.Azure.Tests.Queue;

public class LegacyAzureStorageQueueTests : QueueTestBase
{
    private readonly string _queueName = "foundatio-legacy-" + Guid.NewGuid().ToString("N").Substring(10);

    public LegacyAzureStorageQueueTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override IQueue<SimpleWorkItem>? GetQueue(int retries = 1, TimeSpan? workItemTimeout = null, TimeSpan? retryDelay = null, int[]? retryMultipliers = null, int deadLetterMaxItems = 100, bool runQueueMaintenance = true, TimeProvider? timeProvider = null, ISerializer? serializer = null, Func<SimpleWorkItem, string?>? groupIdResolver = null)
    {
        string? connectionString = Configuration.GetConnectionString("AzureStorageConnectionString");
        if (String.IsNullOrEmpty(connectionString))
            return null;

        _logger.LogDebug("Queue Id: {Name}", _queueName);
        return new AzureStorageQueue<SimpleWorkItem>(o =>
        {
            o.ConnectionString(connectionString)
                .Name(_queueName)
#pragma warning disable CS0618 // Testing legacy mode intentionally
                .CompatibilityMode(AzureStorageQueueCompatibilityMode.Legacy)
#pragma warning restore CS0618
                .Retries(retries)
                .WorkItemTimeout(workItemTimeout.GetValueOrDefault(TimeSpan.FromMinutes(5)))
                .DequeueInterval(TimeSpan.FromSeconds(1))
                .MetricsPollingInterval(TimeSpan.Zero)
                .TimeProvider(timeProvider)
                .Serializer(serializer)
                .LoggerFactory(Log);

            if (groupIdResolver is not null)
                o.GroupId(groupIdResolver);

            return o;
        });
    }

    protected override Task CleanupQueueAsync(IQueue<SimpleWorkItem> queue)
    {
        // Don't delete the queue, it's super expensive and will be cleaned up later.
        queue?.Dispose();
        return Task.CompletedTask;
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

    [Fact(Skip = "Legacy mode does not support CorrelationId or Properties - only message body is persisted")]
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
    public override Task DequeueAsync_AfterAbandonWithMutatedValue_ReturnsOriginalValueAsync()
    {
        return base.DequeueAsync_AfterAbandonWithMutatedValue_ReturnsOriginalValueAsync();
    }

    [Fact]
    public override Task CanDequeueWithCancelledTokenAsync()
    {
        return base.CanDequeueWithCancelledTokenAsync();
    }

    [Fact(Skip = "Dequeue Time takes forever")]
    public override Task CanDequeueEfficientlyAsync()
    {
        return base.CanDequeueEfficientlyAsync();
    }

    [Fact(Skip = "Dequeue Time takes forever")]
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

    [Fact(Skip = "Legacy mode does not support GroupId")]
    public override Task AbandonAsync_WithGroupId_PreservesGroupIdOnRetryAsync()
    {
        return base.AbandonAsync_WithGroupId_PreservesGroupIdOnRetryAsync();
    }

    [Fact(Skip = "Legacy mode does not support GroupId")]
    public override Task AbandonAsync_WithGroupIdAndRetryDelay_PreservesGroupIdOnRetryAsync()
    {
        return base.AbandonAsync_WithGroupIdAndRetryDelay_PreservesGroupIdOnRetryAsync();
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

    [Fact(Skip = "Legacy mode does not support GroupId")]
    public override Task EnqueueAsync_WithGroupId_RoundTripsGroupIdAsync()
    {
        return base.EnqueueAsync_WithGroupId_RoundTripsGroupIdAsync();
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
        return AssertResolvedGroupIdIsNotPersistedAsync(new QueueEntryOptions { GroupId = String.Empty }, "tenant-1", "tenant-1");
    }

    [Fact]
    public override Task EnqueueAsync_WithExplicitGroupId_OverridesResolverAsync()
    {
        return AssertResolvedGroupIdIsNotPersistedAsync(new QueueEntryOptions { GroupId = "explicit" }, "tenant-1", "explicit");
    }

    [Fact]
    public override Task EnqueueAsync_WithGroupIdResolver_DoesNotChangeCallerOptionsAsync()
    {
        return base.EnqueueAsync_WithGroupIdResolver_DoesNotChangeCallerOptionsAsync();
    }

    [Fact]
    public override Task EnqueueAsync_WithGroupIdResolver_UsesPayloadValueAsync()
    {
        return AssertResolvedGroupIdIsNotPersistedAsync(null, "tenant-1", "tenant-1");
    }

    [Fact]
    public override async Task EnqueueAsync_WithReusedOptionsAndGroupIdResolver_EnqueuesEachMessageAsync()
    {
        // Arrange
        using var queue = GetQueue(groupIdResolver: w => w.Data);
        if (queue is null)
            return;

        try
        {
            await queue.DeleteQueueAsync();
            await AssertEmptyQueueAsync(queue);
            var options = new QueueEntryOptions();
            var handlerGroupIds = new List<string?>();
            queue.Enqueuing.AddHandler((_, args) =>
            {
                handlerGroupIds.Add(args.Options.GroupId);
                return Task.CompletedTask;
            });

            // Act
            await queue.EnqueueAsync(new SimpleWorkItem { Data = "tenant-1" }, options);
            await queue.EnqueueAsync(new SimpleWorkItem { Data = "tenant-2" }, options);

            // Assert
            Assert.Equal(["tenant-1", "tenant-2"], handlerGroupIds);
            var dequeuedData = new List<string?>();
            for (int i = 0; i < 2; i++)
            {
                var workItem = await queue.DequeueAsync(TimeSpan.FromSeconds(5));
                Assert.NotNull(workItem);
                Assert.Null(workItem.GroupId);
                dequeuedData.Add(workItem.Value.Data);
                await workItem.CompleteAsync();
            }

            Assert.Equal(["tenant-1", "tenant-2"], dequeuedData.Order());
        }
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    private async Task AssertResolvedGroupIdIsNotPersistedAsync(QueueEntryOptions? options, string data, string expectedGroupId)
    {
        // Arrange
        using var queue = GetQueue(groupIdResolver: w => w.Data);
        if (queue is null)
            return;

        try
        {
            await queue.DeleteQueueAsync();
            await AssertEmptyQueueAsync(queue);
            string? handlerGroupId = null;
            queue.Enqueuing.AddHandler((_, args) =>
            {
                handlerGroupId = args.Options.GroupId;
                return Task.CompletedTask;
            });

            // Act
            await queue.EnqueueAsync(new SimpleWorkItem { Data = data }, options);

            // Assert
            Assert.Equal(expectedGroupId, handlerGroupId);
            var workItem = await queue.DequeueAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(workItem);
            Assert.Equal(data, workItem.Value.Data);
            Assert.Null(workItem.GroupId);
            await workItem.CompleteAsync();
        }
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    [Fact]
    public override Task EnqueueAsync_WithSerializationError_ThrowsAndLeavesQueueEmptyAsync()
    {
        return base.EnqueueAsync_WithSerializationError_ThrowsAndLeavesQueueEmptyAsync();
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

    [Fact(Skip = "Azure Storage Queue handles visibility timeout natively; no client-side auto-abandon")]
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

    [Fact(Skip = "Azure Storage Queue handles visibility timeout natively; no client-side auto-abandon")]
    public override Task CanHandleAutoAbandonInWorker()
    {
        return base.CanHandleAutoAbandonInWorker();
    }

    [Fact]
    public override Task AbandonAsync_WhenRetriesExceeded_MovesToDeadletterAsync()
    {
        return base.AbandonAsync_WhenRetriesExceeded_MovesToDeadletterAsync();
    }
}
