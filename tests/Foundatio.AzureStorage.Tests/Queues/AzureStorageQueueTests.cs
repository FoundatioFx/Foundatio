using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Azure.Storage.Queues;
using Foundatio.Queues;
using Foundatio.Serializer;
using Foundatio.Tests.Queue;
using Foundatio.Tests.Utility;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Foundatio.Azure.Tests.Queue;

public class AzureStorageQueueTests : QueueTestBase
{
    private readonly string _queueName = "foundatio-" + Guid.NewGuid().ToString("N").Substring(10);

    public AzureStorageQueueTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override IQueue<SimpleWorkItem>? GetQueue(int retries = 1, TimeSpan? workItemTimeout = null, TimeSpan? retryDelay = null, int[]? retryMultipliers = null, int deadLetterMaxItems = 100, bool runQueueMaintenance = true, TimeProvider? timeProvider = null, ISerializer? serializer = null)
    {
        string? connectionString = Configuration.GetConnectionString("AzureStorageConnectionString");
        if (String.IsNullOrEmpty(connectionString))
            return null;

        _logger.LogDebug("Queue Id: {Name}", _queueName);
        return new AzureStorageQueue<SimpleWorkItem>(o => o
            .ConnectionString(connectionString)
            .Name(_queueName)
            .Retries(retries)
            .WorkItemTimeout(workItemTimeout.GetValueOrDefault(TimeSpan.FromMinutes(5)))
            .DequeueInterval(TimeSpan.FromSeconds(1))
            .MetricsPollingInterval(TimeSpan.Zero)
            .TimeProvider(timeProvider)
            .Serializer(serializer)
            .LoggerFactory(Log));
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
    public override Task DequeueAsync_AfterAbandonWithMutatedValue_ReturnsOriginalValueAsync()
    {
        return base.DequeueAsync_AfterAbandonWithMutatedValue_ReturnsOriginalValueAsync();
    }

    [Fact]
    public override Task DequeueAsync_WithPoisonMessage_MovesToDeadletterAsync()
    {
        return base.DequeueAsync_WithPoisonMessage_MovesToDeadletterAsync();
    }

    [Fact]
    public override Task EnqueueAsync_WithSerializationError_ThrowsAndLeavesQueueEmptyAsync()
    {
        return base.EnqueueAsync_WithSerializationError_ThrowsAndLeavesQueueEmptyAsync();
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
    public async Task DequeueAsync_WithLegacyFormatMessage_DeserializesWithFallbackAsync()
    {
        // Arrange - Inject a raw (non-envelope) message simulating legacy format
        string? connectionString = Configuration.GetConnectionString("AzureStorageConnectionString");
        if (String.IsNullOrEmpty(connectionString))
            return;

        var queueName = "foundatio-" + Guid.NewGuid().ToString("N").Substring(10);
        var poisonQueueName = $"{queueName}-poison";

        using var defaultQueue = new AzureStorageQueue<SimpleWorkItem>(o => o
            .ConnectionString(connectionString)
            .Name(queueName)
            .Retries(1)
            .WorkItemTimeout(TimeSpan.FromSeconds(30))
            .DequeueInterval(TimeSpan.FromSeconds(1))
            .MetricsPollingInterval(TimeSpan.Zero)
            .LoggerFactory(Log));

        // Ensure queue exists by sending and completing a setup message
        await defaultQueue.EnqueueAsync(new SimpleWorkItem { Data = "setup" });
        using var setupCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var setupEntry = await defaultQueue.DequeueAsync(setupCts.Token);
        Assert.NotNull(setupEntry);
        await setupEntry.CompleteAsync();

        // Inject a raw JSON message (no envelope wrapper) using QueueClient directly
        var rawClient = new QueueClient(connectionString, queueName);

        /* language=json */
        const string legacyJson = """{"Data":"legacy-item","Id":42}""";
        await rawClient.SendMessageAsync(new BinaryData(Encoding.UTF8.GetBytes(legacyJson)), cancellationToken: TestCancellationToken);

        // Act - Dequeue using default mode (envelope format, with legacy fallback)
        using var dequeueCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var entry = await defaultQueue.DequeueAsync(dequeueCts.Token);

        // Assert
        Assert.NotNull(entry?.Value);
        Assert.Equal("legacy-item", entry.Value.Data);
        Assert.Equal(42, entry.Value.Id);
        Assert.Null(entry.CorrelationId);
        await entry.CompleteAsync();

        var stats = await defaultQueue.GetQueueStatsAsync();
        _logger.LogInformation("Queue stats: Queued={Queued} Deadletter={Deadletter}", stats.Queued, stats.Deadletter);
        Assert.Equal(0, stats.Deadletter);
        Assert.Equal(0, stats.Queued);

        // Cleanup
        await defaultQueue.DeleteQueueAsync();
        await new QueueClient(connectionString, poisonQueueName).DeleteIfExistsAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public override Task AbandonAsync_WhenRetriesExceeded_MovesToDeadletterAsync()
    {
        return base.AbandonAsync_WhenRetriesExceeded_MovesToDeadletterAsync();
    }

    [Fact]
    public async Task DequeueAsync_WithCorruptMessage_MovesToDeadletterAsync()
    {
        string? connectionString = Configuration.GetConnectionString("AzureStorageConnectionString");
        if (String.IsNullOrEmpty(connectionString))
            return;

        var queueName = "foundatio-" + Guid.NewGuid().ToString("N").Substring(10);
        var poisonQueueName = $"{queueName}-poison";

        using var queue = new AzureStorageQueue<SimpleWorkItem>(o => o
            .ConnectionString(connectionString)
            .Name(queueName)
            .Retries(1)
            .WorkItemTimeout(TimeSpan.FromSeconds(30))
            .DequeueInterval(TimeSpan.FromSeconds(1))
            .MetricsPollingInterval(TimeSpan.Zero)
            .LoggerFactory(Log));

        // Ensure queue exists
        await queue.EnqueueAsync(new SimpleWorkItem { Data = "setup" });
        using var setupCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var setupEntry = await queue.DequeueAsync(setupCts.Token);
        Assert.NotNull(setupEntry);
        await setupEntry.CompleteAsync();

        // Inject a completely invalid message (not valid JSON at all)
        var rawClient = new QueueClient(connectionString, queueName);
        var invalidPayload = new BinaryData(Encoding.UTF8.GetBytes("error: this is not valid json"));
        await rawClient.SendMessageAsync(invalidPayload, cancellationToken: TestContext.Current.CancellationToken);

        // Dequeue enough times to exhaust retries (1 initial attempt + 1 retry = 2 total)
        const int retries = 1;
        for (int attempt = 0; attempt <= retries; attempt++)
        {
            using var dequeueCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var entry = await queue.DequeueAsync(dequeueCts.Token);
            Assert.Null(entry);

            var intermediateStats = await queue.GetQueueStatsAsync();
            _logger.LogInformation("Corrupt message attempt {Attempt}: Queued={Queued} Deadletter={Deadletter} Abandoned={Abandoned}",
                attempt + 1, intermediateStats.Queued, intermediateStats.Deadletter, intermediateStats.Abandoned);
        }

        var stats = await queue.GetQueueStatsAsync();
        _logger.LogInformation("Corrupt message final stats: Queued={Queued} Deadletter={Deadletter} Abandoned={Abandoned}", stats.Queued, stats.Deadletter, stats.Abandoned);
        Assert.Equal(1, stats.Deadletter);
        Assert.Equal(0, stats.Queued);

        // Cleanup
        await queue.DeleteQueueAsync();
        await new QueueClient(connectionString, poisonQueueName).DeleteIfExistsAsync(TestContext.Current.CancellationToken);
    }
}
