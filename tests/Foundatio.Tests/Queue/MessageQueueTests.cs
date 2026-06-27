using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Foundatio;
using Foundatio.AsyncEx;
using Foundatio.Jobs;
using Foundatio.Messaging;
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

        string id = await queue.EnqueueAsync(new PreviewWorkItem { Data = "hello" }, new QueueMessageOptions
        {
            CorrelationId = "corr-123",
            Priority = MessagePriority.High,
            Headers = MessageHeaders.Create([
                new KeyValuePair<string, string>("tenant", "acme")
            ])
        }, cancellationToken);

        var received = await queue.ReceiveAsync<PreviewWorkItem>(new QueueReceiveOptions { MaxWaitTime = TimeSpan.FromSeconds(1) }, cancellationToken);

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
        ], new QueueMessageOptions { Destination = "custom-work" }, cancellationToken);

        var first = await queue.ReceiveAsync<PreviewWorkItem>(new QueueReceiveOptions { Source = "custom-work", MaxWaitTime = TimeSpan.FromSeconds(1) }, cancellationToken);
        var second = await queue.ReceiveAsync<PreviewWorkItem>(new QueueReceiveOptions { Source = "custom-work", MaxWaitTime = TimeSpan.FromSeconds(1) }, cancellationToken);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal("one", first.Message.Data);
        Assert.Equal("two", second.Message.Data);

        await first.CompleteAsync(cancellationToken);
        await second.CompleteAsync(cancellationToken);
    }

    [Fact]
    public async Task AbandonAsync_RedeliversAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var queue = new MessageQueue(new InMemoryMessageTransport());
        await queue.EnqueueAsync(new PreviewWorkItem { Data = "retry" }, cancellationToken: cancellationToken);

        var first = await queue.ReceiveAsync<PreviewWorkItem>(new QueueReceiveOptions { MaxWaitTime = TimeSpan.FromSeconds(1) }, cancellationToken);
        Assert.NotNull(first);

        await first.AbandonAsync(cancellationToken);

        var second = await queue.ReceiveAsync<PreviewWorkItem>(new QueueReceiveOptions { MaxWaitTime = TimeSpan.FromSeconds(1) }, cancellationToken);
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
        var message = await queue.ReceiveAsync<PreviewWorkItem>(new QueueReceiveOptions { MaxWaitTime = TimeSpan.FromSeconds(1) }, cancellationToken);
        Assert.NotNull(message);

        await Assert.ThrowsAsync<NotSupportedException>(async () => await message.RenewLockAsync(cancellationToken: cancellationToken));
    }

    [Fact]
    public async Task ReportProgressAsync_WhenUntracked_ThrowsAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var queue = new MessageQueue(new InMemoryMessageTransport());

        await queue.EnqueueAsync(new PreviewWorkItem { Data = "progress" }, cancellationToken: cancellationToken);
        var message = await queue.ReceiveAsync<PreviewWorkItem>(new QueueReceiveOptions { MaxWaitTime = TimeSpan.FromSeconds(1) }, cancellationToken);
        Assert.NotNull(message);

        await Assert.ThrowsAsync<NotSupportedException>(async () => await message.ReportProgressAsync(50, "half", cancellationToken));
    }

    [Fact]
    public async Task DeadLetterAsync_DeadLettersAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var transport = new InMemoryMessageTransport();
        await using var queue = new MessageQueue(transport);

        await queue.EnqueueAsync(new PreviewWorkItem { Data = "bad" }, cancellationToken: cancellationToken);
        var message = await queue.ReceiveAsync<PreviewWorkItem>(new QueueReceiveOptions { MaxWaitTime = TimeSpan.FromSeconds(1) }, cancellationToken);
        Assert.NotNull(message);

        await message.DeadLetterAsync("validation", cancellationToken);

        var stats = await transport.GetStatsAsync("preview-work-item", cancellationToken);
        Assert.Equal(1, stats.Deadletter);
        Assert.Equal(0, stats.Working);
    }

    [Fact]
    public async Task StartConsumerAsync_WithAutoAck_CompletesMessageAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var transport = new InMemoryMessageTransport();
        await using var queue = new MessageQueue(transport);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        var handled = new AsyncCountdownEvent(1);

        await using var consumer = await queue.StartConsumerAsync<PreviewWorkItem>((message, _) =>
        {
            Assert.Equal("work", message.Message.Data);
            handled.Signal();
            return Task.CompletedTask;
        }, cancellationToken: cts.Token);

        await queue.EnqueueAsync(new PreviewWorkItem { Data = "work" }, cancellationToken: cts.Token);
        await handled.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitForCompletedAsync(transport, "preview-work-item", cancellationToken);
    }

    [Fact]
    public async Task EnqueueAsync_WithDelay_SchedulesThroughRuntimeStoreAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new InMemoryJobRuntimeStore();
        await using var transport = new InMemoryMessageTransport();
        await using var queue = new MessageQueue(transport, new QueueOptions { RuntimeStore = store });
        var processor = CreateDispatchProcessor(store, transport);

        await queue.EnqueueAsync(new PreviewWorkItem { Data = "later" }, new QueueMessageOptions { Delay = TimeSpan.FromMinutes(1) }, cancellationToken);

        var immediate = await queue.ReceiveAsync<PreviewWorkItem>(new QueueReceiveOptions { MaxWaitTime = TimeSpan.FromMilliseconds(50) }, cancellationToken);
        Assert.Null(immediate);

        Assert.Equal(1, await processor.RunDueOccurrencesAsync(DateTimeOffset.UtcNow.AddMinutes(2), cancellationToken: cancellationToken));

        var delayed = await queue.ReceiveAsync<PreviewWorkItem>(new QueueReceiveOptions { MaxWaitTime = TimeSpan.FromSeconds(2) }, cancellationToken);
        Assert.NotNull(delayed);
        Assert.Equal("later", delayed.Message.Data);
        await delayed.CompleteAsync(cancellationToken);
    }

    [Fact]
    public async Task EnqueueAsync_WithDelayAndNoRuntimeStore_ThrowsAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var queue = new MessageQueue(new InMemoryMessageTransport());

        await Assert.ThrowsAsync<MessageQueueException>(async () =>
            await queue.EnqueueAsync(new PreviewWorkItem { Data = "later" }, new QueueMessageOptions { Delay = TimeSpan.FromMinutes(1) }, cancellationToken));
    }

    [Fact]
    public async Task StartConsumerAsync_WithRedeliveryBackoff_SchedulesRetryThroughRuntimeStoreAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new InMemoryJobRuntimeStore();
        await using var transport = new InMemoryMessageTransport();
        await using var queue = new MessageQueue(transport, new QueueOptions { RuntimeStore = store });
        var processor = CreateDispatchProcessor(store, transport);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        var firstAttempt = new AsyncCountdownEvent(1);
        var secondAttempt = new AsyncCountdownEvent(1);
        int attempts = 0;

        await using var consumer = await queue.StartConsumerAsync<PreviewWorkItem>((message, _) =>
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
        }, new QueueConsumerOptions { RedeliveryBackoff = _ => TimeSpan.FromMinutes(1), MaxAttempts = 3 }, cts.Token);

        await queue.EnqueueAsync(new PreviewWorkItem { Data = "retry" }, cancellationToken: cts.Token);
        await firstAttempt.WaitAsync(TimeSpan.FromSeconds(2));

        var immediate = await queue.ReceiveAsync<PreviewWorkItem>(new QueueReceiveOptions { MaxWaitTime = TimeSpan.FromMilliseconds(50) }, cancellationToken);
        Assert.Null(immediate);

        Assert.Equal(1, await processor.RunDueOccurrencesAsync(DateTimeOffset.UtcNow.AddMinutes(2), cancellationToken: cancellationToken));
        await secondAttempt.WaitAsync(TimeSpan.FromSeconds(2));

    }

    [Fact]
    public async Task ReceiveAsync_WithExpiredMessage_DeadLettersAndReturnsNullAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var transport = new InMemoryMessageTransport();
        await using var queue = new MessageQueue(transport);

        await queue.EnqueueAsync(new PreviewWorkItem { Data = "expired" }, new QueueMessageOptions { TimeToLive = TimeSpan.FromMilliseconds(-1) }, cancellationToken);

        var received = await queue.ReceiveAsync<PreviewWorkItem>(new QueueReceiveOptions { MaxWaitTime = TimeSpan.FromMilliseconds(50) }, cancellationToken);
        Assert.Null(received);

        var stats = await transport.GetStatsAsync("preview-work-item", cancellationToken);
        Assert.Equal(1, stats.Deadletter);
    }

    [Fact]
    public async Task ReceiveAsync_WithPoisonPayload_DeadLettersAndThrowsMessageQueueExceptionAsync()
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

        await Assert.ThrowsAsync<MessageQueueException>(async () =>
            await queue.ReceiveAsync<PreviewWorkItem>(new QueueReceiveOptions { MaxWaitTime = TimeSpan.FromSeconds(1) }, cancellationToken));

        var stats = await transport.GetStatsAsync("preview-work-item", cancellationToken);
        Assert.Equal(1, stats.Deadletter);
        Assert.Equal(0, stats.Working);
    }


    [Fact]
    public async Task AddFoundatio_WithInMemoryMessagingAndJobs_RegistersAppFacingServices()
    {
        var services = new ServiceCollection();

        services.AddFoundatio()
            .Messaging.UseInMemory()
            .Jobs.UseInMemoryRuntime();

        await using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<Foundatio.Messaging.IQueue>());
        Assert.NotNull(provider.GetRequiredService<IPubSub>());
        Assert.NotNull(provider.GetRequiredService<IMessageRouter>());
        Assert.NotNull(provider.GetRequiredService<IMessageTopology>());
        Assert.NotNull(provider.GetRequiredService<IJobClient>());
        Assert.NotNull(provider.GetRequiredService<IJobMonitor>());
        Assert.NotNull(provider.GetRequiredService<IJobWorker>());
    }

    [Fact]
    public async Task AddFoundatio_WithRouting_RegistersRouterAndTopologyAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var services = new ServiceCollection();

        services.AddFoundatio()
            .Messaging.ConfigureRouting(r => r
                .UseDefaultQueue("all-work")
                .MapTopic("grouped-events", typeof(IGroupedWorkItem))
                .UseServiceIdentity("billing-service"))
            .UseInMemory();

        await using var provider = services.BuildServiceProvider();

        var router = provider.GetRequiredService<IMessageRouter>();
        Assert.Equal("all-work", router.ResolveRoute(new MessageRouteContext
        {
            MessageType = typeof(PreviewWorkItem),
            Role = MessageRouteRole.QueueDestination
        }));
        Assert.Equal("grouped-events", router.ResolveRoute(new MessageRouteContext
        {
            MessageType = typeof(OtherWorkItem),
            Role = MessageRouteRole.PubSubTopic
        }));

        var topology = provider.GetRequiredService<IMessageTopology>();
        var declarations = topology.GetDeclarations();
        Assert.Contains(declarations, d => d.Role == DestinationRole.Queue && d.Name == "all-work");
        Assert.Contains(declarations, d => d.Role == DestinationRole.Topic && d.Name == "grouped-events");
        Assert.Contains(declarations, d => d.Role == DestinationRole.Subscription && d.Name == "billing-service" && d.Source == "grouped-events");

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await topology.ValidateAsync(cancellationToken));
        await topology.EnsureAsync(cancellationToken);
        await topology.ValidateAsync(cancellationToken);
    }

    [Fact]
    public async Task EnqueueAsync_WithRouteAttribute_UsesAttributedDestinationAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var queue = new MessageQueue(new InMemoryMessageTransport());

        await queue.EnqueueAsync(new RoutedWorkItem { Data = "route" }, cancellationToken: cancellationToken);

        var received = await queue.ReceiveAsync<RoutedWorkItem>(new QueueReceiveOptions { Source = "routed-work", MaxWaitTime = TimeSpan.FromSeconds(1) }, cancellationToken);

        Assert.NotNull(received);
        Assert.Equal("route", received.Message.Data);
        await received.CompleteAsync(cancellationToken);
    }

    [Fact]
    public async Task StartConsumerAsync_WithSameKeyAndSameRegistration_ReturnsExistingConsumerAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var queue = new MessageQueue(new InMemoryMessageTransport());
        Func<IReceivedMessage<PreviewWorkItem>, CancellationToken, Task> handler = (_, _) => Task.CompletedTask;

        await using var first = await queue.StartConsumerAsync(handler, new QueueConsumerOptions { Key = "shared" }, cancellationToken);
        var second = await queue.StartConsumerAsync(handler, new QueueConsumerOptions { Key = "shared" }, cancellationToken);

        Assert.Same(first, second);
    }

    [Fact]
    public async Task StartConsumerAsync_WithSameKeyAndDifferentHandler_ThrowsAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var queue = new MessageQueue(new InMemoryMessageTransport());

        await using var first = await queue.StartConsumerAsync<PreviewWorkItem>((_, _) => Task.CompletedTask, new QueueConsumerOptions { Key = "shared" }, cancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await queue.StartConsumerAsync<PreviewWorkItem>((_, _) => Task.CompletedTask, new QueueConsumerOptions { Key = "shared" }, cancellationToken));
    }

    [Fact]
    public async Task ReceiveAsync_WithGroupedInterfaceRoute_ReturnsRawMessagesAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var routing = new MessageRoutingOptionsBuilder()
            .MapQueue("grouped-work", typeof(IGroupedWorkItem))
            .Build();
        await using var queue = new MessageQueue(new InMemoryMessageTransport(), new QueueOptions { Router = new DefaultMessageRouter(routing) });

        await queue.EnqueueBatchAsync(new object[]
        {
            new PreviewWorkItem { Data = "one" },
            new OtherWorkItem { Data = "two" }
        }, cancellationToken: cancellationToken);

        var first = await queue.ReceiveAsync(new QueueReceiveOptions { RouteType = typeof(IGroupedWorkItem), MaxWaitTime = TimeSpan.FromSeconds(1) }, cancellationToken);
        var second = await queue.ReceiveAsync(new QueueReceiveOptions { RouteType = typeof(IGroupedWorkItem), MaxWaitTime = TimeSpan.FromSeconds(1) }, cancellationToken);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEmpty(first.Body.ToArray());
        Assert.Equal(typeof(PreviewWorkItem).FullName, first.MessageType);
        Assert.Equal(typeof(OtherWorkItem).FullName, second.MessageType);

        await first.CompleteAsync(cancellationToken);
        await second.CompleteAsync(cancellationToken);
    }

    [Fact]
    public async Task ReceiveAsync_WithDefaultQueueRoute_ReturnsRawMessageAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var routing = new MessageRoutingOptionsBuilder()
            .UseDefaultQueue("all-work")
            .Build();
        await using var queue = new MessageQueue(new InMemoryMessageTransport(), new QueueOptions { Router = new DefaultMessageRouter(routing) });

        await queue.EnqueueAsync(new PreviewWorkItem { Data = "global" }, cancellationToken: cancellationToken);

        var received = await queue.ReceiveAsync(new QueueReceiveOptions { MaxWaitTime = TimeSpan.FromSeconds(1) }, cancellationToken);

        Assert.NotNull(received);
        Assert.Equal(typeof(PreviewWorkItem).FullName, received.MessageType);
        await received.CompleteAsync(cancellationToken);
    }


    private static async Task WaitForCompletedAsync(InMemoryMessageTransport transport, string destination, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var stats = await transport.GetStatsAsync(destination, cancellationToken);
            if (stats.Completed == 1)
                return;

            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
        }

        var finalStats = await transport.GetStatsAsync(destination, cancellationToken);
        Assert.Equal(1, finalStats.Completed);
    }

    private static JobScheduleProcessor CreateDispatchProcessor(IJobRuntimeStore store, IMessageTransport transport)
    {
        var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var worker = new JobWorker(store, serviceProvider, nodeId: "node-a");
        return new JobScheduleProcessor(new InMemoryJobScheduler(), store, worker, nodeId: "node-a", transport: transport);
    }

    [MessageRoute("routed-work")]
    private sealed class RoutedWorkItem
    {
        public string? Data { get; set; }
    }

    private interface IGroupedWorkItem
    {
    }

    private sealed class PreviewWorkItem : IGroupedWorkItem
    {
        public string? Data { get; set; }
    }

    private sealed class OtherWorkItem : IGroupedWorkItem
    {
        public string? Data { get; set; }
    }
}