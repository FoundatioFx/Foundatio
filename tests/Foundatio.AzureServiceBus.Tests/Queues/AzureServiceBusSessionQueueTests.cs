using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Queues;
using Foundatio.Serializer;
using Foundatio.Tests.Queue;
using Foundatio.Tests.Utility;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Foundatio.AzureServiceBus.Tests.Queue;

public class AzureServiceBusSessionQueueTests : QueueTestBase
{
    private const string SessionRequiresGroupId = "Session-enabled queues require a group id";

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
            return "foundatio-test-queue-sessions";

        return "foundatio-sessions-" + Guid.NewGuid().ToString("N")[..10];
    }

    public AzureServiceBusSessionQueueTests(ITestOutputHelper output) : base(output)
    {
        Log.SetLogLevel<AzureServiceBusQueue<SimpleWorkItem>>(LogLevel.Trace);

        if (_isEmulator)
            _assertStats = false;
    }

    protected override IQueue<SimpleWorkItem>? GetQueue(int retries = 1, TimeSpan? workItemTimeout = null, TimeSpan? retryDelay = null, int[]? retryMultipliers = null, int deadLetterMaxItems = 100, bool runQueueMaintenance = true, TimeProvider? timeProvider = null, ISerializer? serializer = null, Func<SimpleWorkItem, string?>? groupIdResolver = null)
    {
        string? connectionString = Configuration.GetConnectionString("AzureServiceBusConnectionString");
        if (String.IsNullOrEmpty(connectionString))
            return null;

        var queue = new AzureServiceBusQueue<SimpleWorkItem>(o =>
        {
            o.ConnectionString(connectionString)
             .Name(_queueName)
             .RequiresSession(true)
             .Retries(retries)
             .WorkItemTimeout(workItemTimeout.GetValueOrDefault(TimeSpan.FromMinutes(1)))
             .DequeueInterval(TimeSpan.FromMilliseconds(500))
             .ReadQueueTimeout(TimeSpan.FromSeconds(2))
             .TimeProvider(timeProvider)
             .MetricsPollingInterval(TimeSpan.Zero)
             .Serializer(serializer)
             .LoggerFactory(Log);

            if (retryDelay.HasValue)
                o.RetryDelay(_ => retryDelay.Value);

            if (groupIdResolver is not null)
                o.GroupId(groupIdResolver);

            if (!_isEmulator)
                o.AutoDeleteOnIdle(TimeSpan.FromMinutes(5));

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
    public async Task DequeueAsync_WithSessions_DeliversEachGroupInOrderOneAtATimeAsync()
    {
        // Arrange
        using var queue = GetQueue(groupIdResolver: w => w.Data?.Split('-')[0]);
        if (queue is null)
            return;

        try
        {
            await queue.DeleteQueueAsync();
            await AssertEmptyQueueAsync(queue);
            await queue.EnqueueAsync(new SimpleWorkItem { Data = "a-1" });
            await queue.EnqueueAsync(new SimpleWorkItem { Data = "a-2" });
            await queue.EnqueueAsync(new SimpleWorkItem { Data = "b-1" });

            // Act
            var first = await queue.DequeueAsync(TimeSpan.FromSeconds(10));
            var second = await queue.DequeueAsync(TimeSpan.FromSeconds(10));

            // Assert: while the first group's message is in flight, the next dequeue gets the other group
            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.NotEqual(first.GroupId, second.GroupId);

            var received = new List<string?> { first.Value.Data, second.Value.Data };
            await first.CompleteAsync();
            await second.CompleteAsync();

            var third = await queue.DequeueAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(third);
            received.Add(third.Value.Data);
            await third.CompleteAsync();

            Assert.Equal(["a-1", "a-2"], received.FindAll(d => d?.StartsWith("a-") is true));
            Assert.Contains("b-1", received);
        }
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    [Fact]
    public async Task SessionEntry_RenewAbandonAndComplete_SettleOnSessionReceiverAsync()
    {
        // Arrange
        using var queue = GetQueue(retries: 2);
        if (queue is null)
            return;

        try
        {
            await queue.DeleteQueueAsync();
            await AssertEmptyQueueAsync(queue);
            await queue.EnqueueAsync(new SimpleWorkItem { Data = "settle" }, new QueueEntryOptions { GroupId = "tenant-1" });

            // Act
            var entry = await queue.DequeueAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(entry);
            await entry.RenewLockAsync();
            await entry.AbandonAsync();

            var retry = await queue.DequeueAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(retry);
            await retry.RenewLockAsync();
            await retry.CompleteAsync();

            // Assert
            Assert.Equal("tenant-1", retry.GroupId);
            Assert.Equal(2, retry.Attempts);
            Assert.True(retry.IsCompleted);
            Assert.Null(await queue.DequeueAsync(TimeSpan.FromSeconds(3)));
        }
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    [Fact]
    public async Task AbandonAsync_WithRetryDelay_KeepsRetryAheadOfLaterMessagesInGroupAsync()
    {
        // Arrange
        using var queue = GetQueue(retries: 2, retryDelay: TimeSpan.FromSeconds(5));
        if (queue is null)
            return;

        try
        {
            await queue.DeleteQueueAsync();
            await AssertEmptyQueueAsync(queue);
            await queue.EnqueueAsync(new SimpleWorkItem { Data = "a-1" }, new QueueEntryOptions { GroupId = "a" });
            await queue.EnqueueAsync(new SimpleWorkItem { Data = "a-2" }, new QueueEntryOptions { GroupId = "a" });

            // Act
            var first = await queue.DequeueAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(first);
            Assert.Equal("a-1", first.Value.Data);
            await first.AbandonAsync();

            var next = await queue.DequeueAsync(TimeSpan.FromSeconds(10));

            // Assert
            Assert.NotNull(next);
            Assert.Equal("a-1", next.Value.Data);
            Assert.Equal(2, next.Attempts);
            await next.CompleteAsync();

            var last = await queue.DequeueAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(last);
            Assert.Equal("a-2", last.Value.Data);
            await last.CompleteAsync();
        }
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    [Fact]
    public async Task EnqueueAsync_WithoutGroupId_ThrowsBeforeSendingAsync()
    {
        // Arrange
        using var queue = GetQueue();
        if (queue is null)
            return;

        try
        {
            // Act
            var exception = await Assert.ThrowsAsync<QueueException>(() => queue.EnqueueAsync(new SimpleWorkItem { Data = "no-group" }));

            // Assert
            Assert.Contains("GroupId is required", exception.Message);
            Assert.Equal(0, (await queue.GetQueueStatsAsync()).Enqueued);
        }
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    [Fact]
    public async Task EnqueueAsync_WhenEnqueuingHandlerAssignsGroupId_EnqueuesToSessionAsync()
    {
        // Arrange
        using var queue = GetQueue();
        if (queue is null)
            return;

        try
        {
            await queue.DeleteQueueAsync();
            await AssertEmptyQueueAsync(queue);
            queue.Enqueuing.AddHandler((_, args) =>
            {
                args.Options.GroupId ??= "assigned";
                return Task.CompletedTask;
            });

            // Act
            await queue.EnqueueAsync(new SimpleWorkItem { Data = "handler-group" });
            var entry = await queue.DequeueAsync(TimeSpan.FromSeconds(10));

            // Assert
            Assert.NotNull(entry);
            Assert.Equal("assigned", entry.GroupId);
            await entry.CompleteAsync();
        }
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    [Fact]
    public async Task EnqueueAsync_WithGroupIdOverSessionLimit_ThrowsArgumentExceptionAsync()
    {
        // Arrange
        using var queue = GetQueue();
        if (queue is null)
            return;

        try
        {
            // Act & Assert
            await Assert.ThrowsAsync<ArgumentException>(() => queue.EnqueueAsync(new SimpleWorkItem { Data = "long" }, new QueueEntryOptions { GroupId = new string('g', 129) }));
            Assert.Equal(0, (await queue.GetQueueStatsAsync()).Enqueued);
        }
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    [Fact]
    public async Task DequeueAsync_WithZeroTimeout_ReceivesAvailableSessionMessageAsync()
    {
        // Arrange
        using var queue = GetQueue();
        if (queue is null)
            return;

        try
        {
            await queue.DeleteQueueAsync();
            await AssertEmptyQueueAsync(queue);
            await queue.EnqueueAsync(new SimpleWorkItem { Data = "check-once" }, new QueueEntryOptions { GroupId = "tenant-1" });

            // Act
            var entry = await queue.DequeueAsync(TimeSpan.Zero);

            // Assert
            Assert.NotNull(entry);
            Assert.Equal("check-once", entry.Value.Data);
            await entry.CompleteAsync();
        }
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    [Fact]
    public async Task StartWorkingAsync_WhenCancelledDuringHandler_ReleasesSessionAndDeletesQueueAsync()
    {
        // Arrange
        using var queue = GetQueue();
        if (queue is null)
            return;

        try
        {
            await queue.EnqueueAsync(new SimpleWorkItem { Data = "tenant-1" }, new QueueEntryOptions { GroupId = "tenant-1" });

            var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var handlerStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var workerCancellation = new CancellationTokenSource();

            await queue.StartWorkingAsync(async (_, cancellationToken) =>
            {
                handlerStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                finally
                {
                    handlerStopped.TrySetResult();
                }
            }, cancellationToken: workerCancellation.Token);

            await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), TestCancellationToken);

            // Act
            await workerCancellation.CancelAsync();
            await handlerStopped.Task.WaitAsync(TimeSpan.FromSeconds(10), TestCancellationToken);
            await queue.DeleteQueueAsync();

            // Assert
            Assert.Null(await queue.DequeueAsync(TimeSpan.FromSeconds(3)));
        }
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    [Fact(Skip = SessionRequiresGroupId)]
    public override Task EnqueueAsync_WhenEnqueuingHandlerClearsGroupId_EnqueuesWithoutGroupAsync()
    {
        return base.EnqueueAsync_WhenEnqueuingHandlerClearsGroupId_EnqueuesWithoutGroupAsync();
    }

    [Fact(Skip = SessionRequiresGroupId)]
    public override Task EnqueueAsync_WhenResolverReturnsEmpty_EnqueuesWithoutGroupAsync()
    {
        return base.EnqueueAsync_WhenResolverReturnsEmpty_EnqueuesWithoutGroupAsync();
    }

    [Fact(Skip = SessionRequiresGroupId)]
    public override Task EnqueueAsync_WhenResolverReturnsNull_EnqueuesWithoutGroupAsync()
    {
        return base.EnqueueAsync_WhenResolverReturnsNull_EnqueuesWithoutGroupAsync();
    }

    [Fact]
    public override Task EnqueueAsync_WithActivity_AppliesCorrelationBeforeEnqueuingHandlersAsync()
    {
        return base.EnqueueAsync_WithActivity_AppliesCorrelationBeforeEnqueuingHandlersAsync();
    }

    [Fact(Skip = SessionRequiresGroupId)]
    public override Task EnqueueAsync_WithEmptyGroupId_EnqueuesWithoutGroupAsync()
    {
        return base.EnqueueAsync_WithEmptyGroupId_EnqueuesWithoutGroupAsync();
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
    public override Task EnqueueAsync_WithGroupId_RoundTripsGroupIdAsync()
    {
        return base.EnqueueAsync_WithGroupId_RoundTripsGroupIdAsync();
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
    public override Task EnqueueAsync_WithReusedOptions_DoesNotChangeCallerOptionsAsync()
    {
        return base.EnqueueAsync_WithReusedOptions_DoesNotChangeCallerOptionsAsync();
    }

    [Fact]
    public override Task EnqueueAsync_WithReusedOptionsAndGroupIdResolver_EnqueuesEachMessageAsync()
    {
        return base.EnqueueAsync_WithReusedOptionsAndGroupIdResolver_EnqueuesEachMessageAsync();
    }
}
