using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.AsyncEx;
using Foundatio.Jobs;
using Foundatio.Messaging;
using Foundatio.Queues;
using Foundatio.Tests.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Foundatio.Tests.Queue;

public class MessageQueueTests
{
    [Fact]
    public async Task EnqueueAsync_WithOptions_CanReceiveAndCompleteAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var transport = new InMemoryMessageTransport();
        await using var queue = new MessageQueue(transport);

        string id = await queue.EnqueueAsync(new PreviewWorkItem { Data = "hello" }, new EnqueueOptions
        {
            CorrelationId = "corr-123",
            Priority = MessagePriority.High,
            Headers = MessageHeaders.Create([
                new KeyValuePair<string, string>("tenant", "acme")
            ])
        }, cancellationToken);

        var received = await queue.ReceiveAsync<PreviewWorkItem>(new ReceiveOptions { MaxWaitTime = TimeSpan.FromSeconds(1) }, cancellationToken);

        Assert.NotNull(received);
        Assert.Equal(id, received.Id);
        Assert.Equal("hello", received.Message.Data);
        Assert.Equal("corr-123", received.CorrelationId);
        Assert.Equal(MessagePriority.High, received.Priority);
        Assert.Equal(1, received.Attempts);
        Assert.Equal("acme", received.Headers["tenant"]);
        Assert.Equal(typeof(PreviewWorkItem).FullName, received.MessageType);

        await received.CompleteAsync(cancellationToken);

        var stats = await transport.GetStatsAsync("preview-work-item", cancellationToken);
        Assert.Equal(1, stats.Completed);
        Assert.Equal(0, stats.Working);
    }

    [Fact]
    public async Task EnqueueBatchAsync_UsesDestinationOverrideAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var transport = new InMemoryMessageTransport();
        await using var queue = new MessageQueue(transport);

        await queue.EnqueueBatchAsync([
            new PreviewWorkItem { Data = "one" },
            new PreviewWorkItem { Data = "two" }
        ], new EnqueueOptions { Destination = "custom-work" }, cancellationToken);

        var first = await queue.ReceiveAsync<PreviewWorkItem>(new ReceiveOptions { Source = "custom-work", MaxWaitTime = TimeSpan.FromSeconds(1) }, cancellationToken);
        var second = await queue.ReceiveAsync<PreviewWorkItem>(new ReceiveOptions { Source = "custom-work", MaxWaitTime = TimeSpan.FromSeconds(1) }, cancellationToken);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal("one", first.Message.Data);
        Assert.Equal("two", second.Message.Data);

        await first.CompleteAsync(cancellationToken);
        await second.CompleteAsync(cancellationToken);
    }

    [Fact]
    public async Task RejectAsync_WithRetry_RedeliversAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var queue = new MessageQueue(new InMemoryMessageTransport());
        await queue.EnqueueAsync(new PreviewWorkItem { Data = "retry" }, cancellationToken: cancellationToken);

        var first = await queue.ReceiveAsync<PreviewWorkItem>(new ReceiveOptions { MaxWaitTime = TimeSpan.FromSeconds(1) }, cancellationToken);
        Assert.NotNull(first);

        await first.RejectAsync(cancellationToken: cancellationToken);

        var second = await queue.ReceiveAsync<PreviewWorkItem>(new ReceiveOptions { MaxWaitTime = TimeSpan.FromSeconds(1) }, cancellationToken);
        Assert.NotNull(second);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(2, second.Attempts);
        Assert.Equal("retry", second.Message.Data);

        await second.CompleteAsync(cancellationToken);
    }

    [Fact]
    public async Task RenewLockAsync_WhenUnsupported_ThrowsAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var queue = new MessageQueue(new InMemoryMessageTransport());

        await queue.EnqueueAsync(new PreviewWorkItem { Data = "lock" }, cancellationToken: cancellationToken);
        var message = await queue.ReceiveAsync<PreviewWorkItem>(new ReceiveOptions { MaxWaitTime = TimeSpan.FromSeconds(1) }, cancellationToken);
        Assert.NotNull(message);

        await Assert.ThrowsAsync<NotSupportedException>(async () => await message.RenewLockAsync(cancellationToken: cancellationToken));
    }

    [Fact]
    public async Task ReportProgressAsync_WhenUntracked_ThrowsAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var queue = new MessageQueue(new InMemoryMessageTransport());

        await queue.EnqueueAsync(new PreviewWorkItem { Data = "progress" }, cancellationToken: cancellationToken);
        var message = await queue.ReceiveAsync<PreviewWorkItem>(new ReceiveOptions { MaxWaitTime = TimeSpan.FromSeconds(1) }, cancellationToken);
        Assert.NotNull(message);

        await Assert.ThrowsAsync<NotSupportedException>(async () => await message.ReportProgressAsync(50, "half", cancellationToken));
    }

    [Fact]
    public async Task RejectAsync_WithoutRetry_DeadLettersAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var transport = new InMemoryMessageTransport();
        await using var queue = new MessageQueue(transport);

        await queue.EnqueueAsync(new PreviewWorkItem { Data = "bad" }, cancellationToken: cancellationToken);
        var message = await queue.ReceiveAsync<PreviewWorkItem>(new ReceiveOptions { MaxWaitTime = TimeSpan.FromSeconds(1) }, cancellationToken);
        Assert.NotNull(message);

        await message.RejectAsync(retry: false, reason: "validation", cancellationToken: cancellationToken);

        var stats = await transport.GetStatsAsync("preview-work-item", cancellationToken);
        Assert.Equal(1, stats.Deadletter);
        Assert.Equal(0, stats.Working);
    }

    [Fact]
    public async Task StartWorkingAsync_WithAutoAck_CompletesMessageAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var transport = new InMemoryMessageTransport();
        await using var queue = new MessageQueue(transport);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        var handled = new AsyncCountdownEvent(1);

        var worker = queue.StartWorkingAsync<PreviewWorkItem>((message, _) =>
        {
            Assert.Equal("work", message.Message.Data);
            handled.Signal();
            return Task.CompletedTask;
        }, cancellationToken: cts.Token);

        await queue.EnqueueAsync(new PreviewWorkItem { Data = "work" }, cancellationToken: cts.Token);
        await handled.WaitAsync(TimeSpan.FromSeconds(2));
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await worker);

        var stats = await transport.GetStatsAsync("preview-work-item", cancellationToken);
        Assert.Equal(1, stats.Completed);
    }

    [Fact]
    public async Task EnqueueAsync_WithDelay_SchedulesThroughRuntimeStoreAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new InMemoryJobRuntimeStore();
        await using var transport = new InMemoryMessageTransport();
        await using var queue = new MessageQueue(transport, new MessageQueueOptions { RuntimeStore = store });
        var processor = CreateDispatchProcessor(store, transport);

        await queue.EnqueueAsync(new PreviewWorkItem { Data = "later" }, new EnqueueOptions { Delay = TimeSpan.FromMinutes(1) }, cancellationToken);

        var immediate = await queue.ReceiveAsync<PreviewWorkItem>(new ReceiveOptions { MaxWaitTime = TimeSpan.FromMilliseconds(50) }, cancellationToken);
        Assert.Null(immediate);

        Assert.Equal(1, await processor.RunDueOccurrencesAsync(DateTimeOffset.UtcNow.AddMinutes(2), cancellationToken: cancellationToken));

        var delayed = await queue.ReceiveAsync<PreviewWorkItem>(new ReceiveOptions { MaxWaitTime = TimeSpan.FromSeconds(2) }, cancellationToken);
        Assert.NotNull(delayed);
        Assert.Equal("later", delayed.Message.Data);
        await delayed.CompleteAsync(cancellationToken);
    }

    [Fact]
    public async Task EnqueueAsync_WithDelayAndNoRuntimeStore_ThrowsAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var queue = new MessageQueue(new InMemoryMessageTransport());

        await Assert.ThrowsAsync<QueueException>(async () =>
            await queue.EnqueueAsync(new PreviewWorkItem { Data = "later" }, new EnqueueOptions { Delay = TimeSpan.FromMinutes(1) }, cancellationToken));
    }

    [Fact]
    public async Task StartWorkingAsync_WithRedeliveryBackoff_SchedulesRetryThroughRuntimeStoreAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new InMemoryJobRuntimeStore();
        await using var transport = new InMemoryMessageTransport();
        await using var queue = new MessageQueue(transport, new MessageQueueOptions { RuntimeStore = store });
        var processor = CreateDispatchProcessor(store, transport);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        var firstAttempt = new AsyncCountdownEvent(1);
        var secondAttempt = new AsyncCountdownEvent(1);
        int attempts = 0;

        var worker = queue.StartWorkingAsync<PreviewWorkItem>((message, _) =>
        {
            attempts++;
            if (attempts == 1)
            {
                Assert.Equal(1, message.Attempts);
                firstAttempt.Signal();
                throw new InvalidOperationException("try again later");
            }

            Assert.Equal(2, message.Attempts);
            Assert.Equal("retry", message.Message.Data);
            secondAttempt.Signal();
            return Task.CompletedTask;
        }, new WorkerOptions { RedeliveryBackoff = _ => TimeSpan.FromMinutes(1), MaxAttempts = 3 }, cts.Token);

        await queue.EnqueueAsync(new PreviewWorkItem { Data = "retry" }, cancellationToken: cts.Token);
        await firstAttempt.WaitAsync(TimeSpan.FromSeconds(2));

        var immediate = await queue.ReceiveAsync<PreviewWorkItem>(new ReceiveOptions { MaxWaitTime = TimeSpan.FromMilliseconds(50) }, cancellationToken);
        Assert.Null(immediate);

        Assert.Equal(1, await processor.RunDueOccurrencesAsync(DateTimeOffset.UtcNow.AddMinutes(2), cancellationToken: cancellationToken));
        await secondAttempt.WaitAsync(TimeSpan.FromSeconds(2));

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await worker);
    }

    [Fact]
    public async Task ReceiveAsync_WithExpiredMessage_DeadLettersAndReturnsNullAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var transport = new InMemoryMessageTransport();
        await using var queue = new MessageQueue(transport);

        await queue.EnqueueAsync(new PreviewWorkItem { Data = "expired" }, new EnqueueOptions { TimeToLive = TimeSpan.FromMilliseconds(-1) }, cancellationToken);

        var received = await queue.ReceiveAsync<PreviewWorkItem>(new ReceiveOptions { MaxWaitTime = TimeSpan.FromMilliseconds(50) }, cancellationToken);
        Assert.Null(received);

        var stats = await transport.GetStatsAsync("preview-work-item", cancellationToken);
        Assert.Equal(1, stats.Deadletter);
    }

    [Fact]
    public async Task ReceiveAsync_WithPoisonPayload_DeadLettersAndThrowsQueueExceptionAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var transport = new InMemoryMessageTransport();
        await using var queue = new MessageQueue(transport);

        await transport.SendAsync("preview-work-item", [
            new TransportMessage
            {
                Body = "not-json"u8.ToArray(),
                Headers = MessageHeaders.Create([
                    new KeyValuePair<string, string>(KnownHeaders.MessageType, typeof(PreviewWorkItem).FullName!)
                ])
            }
        ], new TransportSendOptions(), cancellationToken);

        await Assert.ThrowsAsync<QueueException>(async () =>
            await queue.ReceiveAsync<PreviewWorkItem>(new ReceiveOptions { MaxWaitTime = TimeSpan.FromSeconds(1) }, cancellationToken));

        var stats = await transport.GetStatsAsync("preview-work-item", cancellationToken);
        Assert.Equal(1, stats.Deadletter);
        Assert.Equal(0, stats.Working);
    }


    private static JobScheduleProcessor CreateDispatchProcessor(IJobRuntimeStore store, IMessageTransport transport)
    {
        var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var client = new JobClient(store, serviceProvider, nodeId: "node-a");
        return new JobScheduleProcessor(new InMemoryJobScheduler(), store, client, nodeId: "node-a", transport: transport);
    }

    private sealed class PreviewWorkItem
    {
        public string? Data { get; set; }
    }
}