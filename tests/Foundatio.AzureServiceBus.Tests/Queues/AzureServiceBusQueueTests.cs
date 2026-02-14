using System;
using System.Threading.Tasks;
using Foundatio.Queues;
using Foundatio.Tests.Queue;
using Foundatio.Tests.Utility;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Foundatio.AzureServiceBus.Tests.Queue;

public class AzureServiceBusQueueTests : QueueTestBase
{
    // Azure Service Bus Emulator limitations documentation:
    // https://learn.microsoft.com/en-us/azure/service-bus-messaging/test-locally-with-service-bus-emulator
    // The emulator does not support the Service Bus Management HTTP API for runtime queue stats,
    // and has connection quota limits that affect multi-instance tests.
    private const string EmulatorLimitationsUrl = "https://learn.microsoft.com/en-us/azure/service-bus-messaging/test-locally-with-service-bus-emulator";

    private static readonly bool _isEmulator = IsEmulator();
    private static readonly string _queueName = GetQueueName();

    private static bool IsEmulator()
    {
        string connectionString = Configuration.GetConnectionString("AzureServiceBusConnectionString");
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

        // Disable stats assertions when using the emulator since admin API is not available
        if (_isEmulator)
            _assertStats = false;
    }

    protected override IQueue<SimpleWorkItem> GetQueue(int retries = 1, TimeSpan? workItemTimeout = null, TimeSpan? retryDelay = null, int[] retryMultipliers = null, int deadLetterMaxItems = 100, bool runQueueMaintenance = true, TimeProvider timeProvider = null)
    {
        string connectionString = Configuration.GetConnectionString("AzureServiceBusConnectionString");
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
             .LoggerFactory(Log);

            // Configure retry delay if provided
            if (retryDelay.HasValue)
                o.RetryDelay(_ => retryDelay.Value);

            // These options are not supported by the emulator
            if (!_isEmulator)
            {
                o.AutoDeleteOnIdle(TimeSpan.FromMinutes(5))
                 .EnableBatchedOperations(true)
                 .EnablePartitioning(false) // Disabled to ensure consistent message delivery
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

        // Only drain the queue when using the emulator to ensure test isolation
        // Don't delete the queue on real Azure Service Bus - it's expensive and will be cleaned up later
        if (_isEmulator)
            await queue.DeleteQueueAsync();

        queue.Dispose();
    }

    [Fact]
    public override Task CanQueueAndDequeueWorkItemAsync()
    {
        return base.CanQueueAndDequeueWorkItemAsync();
    }

    [Fact]
    public override Task CanDequeueWithCancelledTokenAsync()
    {
        return base.CanDequeueWithCancelledTokenAsync();
    }

    [Fact]
    public override async Task CanQueueAndDequeueMultipleWorkItemsAsync()
    {
        // Skip this test when using the emulator because the test harness checks
        // queue stats (Queued count) which requires the admin API that the emulator doesn't support.
        // See: https://learn.microsoft.com/en-us/azure/service-bus-messaging/test-locally-with-service-bus-emulator
        if (_isEmulator)
        {
            _logger.LogWarning("Skipping {TestName}: Azure Service Bus Emulator does not support the Management HTTP API for queue statistics. See {Url}",
                nameof(CanQueueAndDequeueMultipleWorkItemsAsync), EmulatorLimitationsUrl);
            return;
        }

        await base.CanQueueAndDequeueMultipleWorkItemsAsync();
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

    [Fact(Skip = "Dequeue Time takes forever")]
    public override Task WorkItemsWillTimeoutAsync()
    {
        return base.WorkItemsWillTimeoutAsync();
    }

    [Fact(Skip = "Dequeue Time takes forever")]
    public override Task WillNotWaitForItemAsync()
    {
        return base.WillNotWaitForItemAsync();
    }

    [Fact]
    public override Task WorkItemsWillGetMovedToDeadletterAsync()
    {
        return base.WorkItemsWillGetMovedToDeadletterAsync();
    }

    [Fact(Skip = "Dequeue Time takes forever")]
    public override Task CanResumeDequeueEfficientlyAsync()
    {
        return base.CanResumeDequeueEfficientlyAsync();
    }

    [Fact(Skip = "Dequeue Time takes forever")]
    public override Task CanDequeueEfficientlyAsync()
    {
        return base.CanDequeueEfficientlyAsync();
    }

    [Fact]
    public override Task CanDequeueWithLockingAsync()
    {
        return base.CanDequeueWithLockingAsync();
    }

    [Fact]
    public override async Task CanHaveMultipleQueueInstancesWithLockingAsync()
    {
        // Skip this test when using the emulator - the test uses retries: 0 which requires
        // MaxDeliveryCount: 1, but other tests require higher MaxDeliveryCount values.
        // Since the emulator doesn't support dynamic queue configuration, we can't satisfy both.
        // The emulator is configured with MaxDeliveryCount: 5 to support retry tests.
        // See emulator limitations: https://learn.microsoft.com/en-us/azure/service-bus-messaging/overview-emulator#known-limitations
        if (_isEmulator)
        {
            _logger.LogWarning("Skipping {TestName}: Azure Service Bus Emulator uses fixed MaxDeliveryCount which conflicts with this test's retries:0 requirement. See {Url}",
                nameof(CanHaveMultipleQueueInstancesWithLockingAsync), EmulatorLimitationsUrl);
            return;
        }

        await base.CanHaveMultipleQueueInstancesWithLockingAsync();
    }

    [Fact]
    public override Task CanAutoCompleteWorkerAsync()
    {
        return base.CanAutoCompleteWorkerAsync();
    }

    [Fact]
    public override async Task CanHaveMultipleQueueInstancesAsync()
    {
        // Skip this test when using the emulator - the test uses retries: 0 which requires
        // MaxDeliveryCount: 1, but other tests require higher MaxDeliveryCount values.
        // Since the emulator doesn't support dynamic queue configuration, we can't satisfy both.
        // The emulator is configured with MaxDeliveryCount: 5 to support retry tests.
        // See emulator limitations: https://learn.microsoft.com/en-us/azure/service-bus-messaging/overview-emulator#known-limitations
        if (_isEmulator)
        {
            _logger.LogWarning("Skipping {TestName}: Azure Service Bus Emulator uses fixed MaxDeliveryCount which conflicts with this test's retries:0 requirement. See {Url}",
                nameof(CanHaveMultipleQueueInstancesAsync), EmulatorLimitationsUrl);
            return;
        }

        await base.CanHaveMultipleQueueInstancesAsync();
    }

    [Fact]
    public override Task CanRunWorkItemWithMetricsAsync()
    {
        return base.CanRunWorkItemWithMetricsAsync();
    }

    [Fact(Skip = "Dequeue Time takes forever")]
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

    [Fact(Skip = "Not using this test because you can set specific delay times for servicebus")]
    public override Task CanDelayRetryAsync()
    {
        return base.CanDelayRetryAsync();
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
}
