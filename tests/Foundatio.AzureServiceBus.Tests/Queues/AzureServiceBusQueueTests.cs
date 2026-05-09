using System;
using System.Threading.Tasks;
using Foundatio.Queues;
using Foundatio.Serializer;
using Foundatio.Tests.Queue;
using Foundatio.Tests.Serializer;
using Foundatio.Tests.Utility;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Foundatio.AzureServiceBus.Tests.Queue;

public class AzureServiceBusQueueTests : QueueTestBase
{
    private const string EmulatorLimitationsUrl = "https://learn.microsoft.com/en-us/azure/service-bus-messaging/test-locally-with-service-bus-emulator";

    private static readonly bool _isEmulator = IsEmulator();
    private static readonly string _queueName = GetQueueName();

    private static bool IsEmulator()
    {
        string? connectionString = Configuration.GetConnectionString("AzureServiceBusConnectionString");
        return !String.IsNullOrEmpty(connectionString) &&
            connectionString.Contains("UseDevelopmentEmulator=true", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetQueueName()
    {
        if (_isEmulator)
            return "foundatio-test-queue";

        return "foundatio-" + Guid.NewGuid().ToString("N")[..10];
    }

    public AzureServiceBusQueueTests(ITestOutputHelper output) : base(output)
    {
        Log.SetLogLevel<AzureServiceBusQueue<SimpleWorkItem>>(LogLevel.Trace);

        if (_isEmulator)
            _assertStats = false;
    }

    protected override IQueue<SimpleWorkItem>? GetQueue(int retries = 1, TimeSpan? workItemTimeout = null, TimeSpan? retryDelay = null, int[]? retryMultipliers = null, int deadLetterMaxItems = 100, bool runQueueMaintenance = true, TimeProvider? timeProvider = null, ISerializer? serializer = null)
    {
        string? connectionString = Configuration.GetConnectionString("AzureServiceBusConnectionString");
        if (String.IsNullOrEmpty(connectionString))
            return null;

        var queue = new AzureServiceBusQueue<SimpleWorkItem>(o =>
        {
            o.ConnectionString(connectionString)
             .Name(_queueName)
             .Retries(retries)
             .WorkItemTimeout(workItemTimeout.GetValueOrDefault(TimeSpan.FromMinutes(5)))
             .DequeueInterval(TimeSpan.FromMilliseconds(500))
             .ReadQueueTimeout(TimeSpan.FromSeconds(2))
             .TimeProvider(timeProvider)
             .MetricsPollingInterval(TimeSpan.Zero)
             .Serializer(serializer)
             .LoggerFactory(Log);

            if (retryDelay.HasValue)
                o.RetryDelay(_ => retryDelay.Value);

            if (!_isEmulator)
            {
                o.AutoDeleteOnIdle(TimeSpan.FromMinutes(5))
                 .EnableBatchedOperations(true)
                 .EnablePartitioning(false)
                 .RequiresDuplicateDetection(false)
                 .RequiresSession(false);
            }

            return o;
        });

        _logger.LogDebug("Queue Id: {QueueId}", queue.QueueId);
        return queue;
    }

    protected override async Task CleanupQueueAsync(IQueue<SimpleWorkItem> queue)
    {
        if (queue is null)
            return;

        if (_isEmulator)
            await queue.DeleteQueueAsync();

        queue.Dispose();
    }

    [Fact]
    public override Task AbandonAsync_WhenRetriesExceeded_MovesToDeadletterAsync()
    {
        return base.AbandonAsync_WhenRetriesExceeded_MovesToDeadletterAsync();
    }

    [Fact]
    public override Task CanAbandonQueueEntryOnceAsync()
    {
        return base.CanAbandonQueueEntryOnceAsync();
    }

    [Fact]
    public override Task CanAutoCompleteWorkerAsync()
    {
        return base.CanAutoCompleteWorkerAsync();
    }

    [Fact]
    public override Task CanCompleteQueueEntryOnceAsync()
    {
        return base.CanCompleteQueueEntryOnceAsync();
    }

    [Fact(Skip = "Dequeue Time takes forever")]
    public override Task CanDequeueEfficientlyAsync()
    {
        return base.CanDequeueEfficientlyAsync();
    }

    [Fact]
    public override Task CanDequeueWithCancelledTokenAsync()
    {
        return base.CanDequeueWithCancelledTokenAsync();
    }

    [Fact]
    public override Task CanDequeueWithLockingAsync()
    {
        return base.CanDequeueWithLockingAsync();
    }

    [Fact(Skip = "Not using this test because you can set specific delay times for servicebus")]
    public override Task CanDelayRetryAsync()
    {
        return base.CanDelayRetryAsync();
    }

    [Fact]
    public override Task CanDiscardDuplicateQueueEntriesAsync()
    {
        return base.CanDiscardDuplicateQueueEntriesAsync();
    }

    [Fact(Skip = "Azure Service Bus handles visibility timeout natively")]
    public override Task CanHandleAutoAbandonInWorker()
    {
        return base.CanHandleAutoAbandonInWorker();
    }

    [Fact]
    public override Task CanHandleErrorInWorkerAsync()
    {
        return base.CanHandleErrorInWorkerAsync();
    }

    [Fact]
    public override async Task CanHaveMultipleQueueInstancesAsync()
    {
        if (_isEmulator)
        {
            _logger.LogWarning("Skipping {TestName}: Azure Service Bus Emulator uses fixed MaxDeliveryCount which conflicts with this test's retries:0 requirement. See {Url}",
                nameof(CanHaveMultipleQueueInstancesAsync), EmulatorLimitationsUrl);
            return;
        }

        await base.CanHaveMultipleQueueInstancesAsync();
    }

    [Fact]
    public override async Task CanHaveMultipleQueueInstancesWithLockingAsync()
    {
        if (_isEmulator)
        {
            _logger.LogWarning("Skipping {TestName}: Azure Service Bus Emulator uses fixed MaxDeliveryCount which conflicts with this test's retries:0 requirement. See {Url}",
                nameof(CanHaveMultipleQueueInstancesWithLockingAsync), EmulatorLimitationsUrl);
            return;
        }

        await base.CanHaveMultipleQueueInstancesWithLockingAsync();
    }

    [Fact]
    public override async Task CanQueueAndDequeueMultipleWorkItemsAsync()
    {
        if (_isEmulator)
        {
            _logger.LogWarning("Skipping {TestName}: Azure Service Bus Emulator does not support the Management HTTP API for queue statistics. See {Url}",
                nameof(CanQueueAndDequeueMultipleWorkItemsAsync), EmulatorLimitationsUrl);
            return;
        }

        await base.CanQueueAndDequeueMultipleWorkItemsAsync();
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

    [Fact(Skip = "Dequeue Time takes forever")]
    public override Task CanRenewLockAsync()
    {
        return base.CanRenewLockAsync();
    }

    [Fact(Skip = "Dequeue Time takes forever")]
    public override Task CanResumeDequeueEfficientlyAsync()
    {
        return base.CanResumeDequeueEfficientlyAsync();
    }

    [Fact]
    public override Task CanRunWorkItemWithMetricsAsync()
    {
        return base.CanRunWorkItemWithMetricsAsync();
    }

    [Fact]
    public override Task CanUseQueueOptionsAsync()
    {
        return base.CanUseQueueOptionsAsync();
    }

    [Fact]
    public override Task CanUseQueueWorkerAsync()
    {
        return base.CanUseQueueWorkerAsync();
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
    public override async Task DequeueAsync_WithPoisonMessage_MovesToDeadletterAsync()
    {
        if (!_isEmulator)
        {
            await base.DequeueAsync_WithPoisonMessage_MovesToDeadletterAsync();
            return;
        }

        const int retries = 2;
        var faultInjectingSerializer = new FaultInjectingSerializer();
        using var queue = GetQueue(retries: retries, retryDelay: TimeSpan.FromMilliseconds(250), serializer: faultInjectingSerializer);
        if (queue is null)
            return;

        try
        {
            await queue.DeleteQueueAsync();
            await AssertEmptyQueueAsync(queue);

            await queue.EnqueueAsync(new SimpleWorkItem { Data = "poison-test" });
            faultInjectingSerializer.ShouldFailOnDeserialize = true;

            for (int attempt = 0; attempt <= retries; attempt++)
            {
                var entry = await queue.DequeueAsync(TimeSpan.FromSeconds(5));
                Assert.Null(entry);

                var intermediateStats = await queue.GetQueueStatsAsync();
                _logger.LogInformation("Poison message attempt {Attempt}: Queued={Queued} Deadletter={Deadletter} Abandoned={Abandoned}",
                    attempt + 1, intermediateStats.Queued, intermediateStats.Deadletter, intermediateStats.Abandoned);
            }

            var stats = await queue.GetQueueStatsAsync();
            Assert.Equal(retries + 1, stats.Abandoned);
            Assert.Equal(0, stats.Queued);

            var finalEntry = await queue.DequeueAsync(TimeSpan.FromSeconds(2));
            Assert.Null(finalEntry);
        }
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    [Fact]
    public override Task DequeueWaitWillGetSignaledAsync()
    {
        return base.DequeueWaitWillGetSignaledAsync();
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
    public override Task EnqueueAsync_WithUniqueId_UsesProvidedIdAsync()
    {
        return base.EnqueueAsync_WithUniqueId_UsesProvidedIdAsync();
    }

    [Fact]
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
    public override Task VerifyDelayedRetryAttemptsAsync()
    {
        return base.VerifyDelayedRetryAttemptsAsync();
    }

    [Fact]
    public override Task VerifyRetryAttemptsAsync()
    {
        return base.VerifyRetryAttemptsAsync();
    }

    [Fact(Skip = "Dequeue Time takes forever")]
    public override Task WillNotWaitForItemAsync()
    {
        return base.WillNotWaitForItemAsync();
    }

    [Fact]
    public override Task WillWaitForItemAsync()
    {
        return base.WillWaitForItemAsync();
    }

    [Fact(Skip = "Dequeue Time takes forever")]
    public override Task WorkItemsWillTimeoutAsync()
    {
        return base.WorkItemsWillTimeoutAsync();
    }

    [Fact]
    public override Task WorkItemsWillGetMovedToDeadletterAsync()
    {
        return base.WorkItemsWillGetMovedToDeadletterAsync();
    }

}
