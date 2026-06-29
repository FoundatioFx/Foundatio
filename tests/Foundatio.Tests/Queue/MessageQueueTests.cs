using System;
using System.Collections.Concurrent;
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
    public async Task RejectAsync_NonTerminal_RedeliversAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var queue = new MessageQueue(new InMemoryMessageTransport());
        await queue.EnqueueAsync(new PreviewWorkItem { Data = "retry" }, cancellationToken: cancellationToken);

        var first = await queue.ReceiveAsync<PreviewWorkItem>(new QueueReceiveOptions { MaxWaitTime = TimeSpan.FromSeconds(1) }, cancellationToken);
        Assert.NotNull(first);

        await first.RejectAsync(cancellationToken: cancellationToken);

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
        // BasicQueueTransport intentionally does not implement ISupportsLockRenewal, so the core must surface the
        // unsupported capability rather than silently no-op.
        await using var queue = new MessageQueue(new BasicQueueTransport());

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
    public async Task RejectAsync_Terminal_DeadLettersAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var transport = new InMemoryMessageTransport();
        await using var queue = new MessageQueue(transport);

        await queue.EnqueueAsync(new PreviewWorkItem { Data = "bad" }, cancellationToken: cancellationToken);
        var message = await queue.ReceiveAsync<PreviewWorkItem>(new QueueReceiveOptions { MaxWaitTime = TimeSpan.FromSeconds(1) }, cancellationToken);
        Assert.NotNull(message);

        await message.RejectAsync(new RejectOptions { Terminal = true, Reason = "validation" }, cancellationToken);

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
    public async Task StartConsumerAsync_WithManualAck_DoesNotAutoCompleteAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var transport = new InMemoryMessageTransport();
        await using var queue = new MessageQueue(transport);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        var handled = new AsyncCountdownEvent(1);

        await using var consumer = await queue.StartConsumerAsync<PreviewWorkItem>((message, _) =>
        {
            handled.Signal();
            return Task.CompletedTask; // intentionally does NOT settle the message
        }, new QueueConsumerOptions { AckMode = AckMode.Manual }, cts.Token);

        await queue.EnqueueAsync(new PreviewWorkItem { Data = "manual" }, cancellationToken: cts.Token);
        await handled.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(200, cts.Token);

        // Manual ack: the handler ran but did not settle, so the message stays in flight and is not auto-completed.
        var stats = await transport.GetStatsAsync("preview-work-item", cancellationToken);
        Assert.Equal(0, stats.Completed);
        Assert.Equal(1, stats.Working);
    }

    [Fact]
    public async Task StartConsumerAsync_WithPoisonMessage_DeadLettersAndKeepsConsumingAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var transport = new InMemoryMessageTransport();
        await using var queue = new MessageQueue(transport);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        var handled = new AsyncCountdownEvent(1);

        await using var consumer = await queue.StartConsumerAsync<PreviewWorkItem>((message, _) =>
        {
            Assert.Equal("good", message.Message.Data);
            handled.Signal();
            return Task.CompletedTask;
        }, cancellationToken: cts.Token);

        // A poison (undeserializable) payload must be dead-lettered without tearing down the consumer loop, so the
        // subsequent valid message is still delivered.
        await transport.SendAsync("preview-work-item", [
            new TransportMessage { Body = System.Text.Encoding.UTF8.GetBytes("}{ not json"), Headers = MessageHeaders.Empty }
        ], new TransportSendOptions(), cts.Token);
        await queue.EnqueueAsync(new PreviewWorkItem { Data = "good" }, cancellationToken: cts.Token);

        await handled.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, handled.CurrentCount);
    }

    [Fact]
    public async Task EnqueueBatchAsync_RespectsTransportMaxBatchSizeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var transport = new BatchLimitTransport(maxBatchSize: 2);
        await using var queue = new MessageQueue(transport);

        await queue.EnqueueBatchAsync(new[]
        {
            new PreviewWorkItem { Data = "1" },
            new PreviewWorkItem { Data = "2" },
            new PreviewWorkItem { Data = "3" },
            new PreviewWorkItem { Data = "4" },
            new PreviewWorkItem { Data = "5" }
        }, cancellationToken: cancellationToken);

        // Five messages to one destination with MaxBatchSize=2 must be split into chunks of 2, 2, 1.
        Assert.Equal(new[] { 2, 2, 1 }, transport.SendBatchSizes);
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
    public async Task EnqueueAsync_WithDelay_RespectsTransportMaxDeliveryDelayAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Within the transport's advertised maximum: delivered natively, never touches the runtime store.
        var nativeStore = new InMemoryJobRuntimeStore();
        await using var nativeTransport = new CappedDelayTransport(maxDeliveryDelay: TimeSpan.FromMinutes(15));
        await using var nativeQueue = new MessageQueue(nativeTransport, new QueueOptions { RuntimeStore = nativeStore });
        var nativeProcessor = CreateDispatchProcessor(nativeStore, nativeTransport);

        await nativeQueue.EnqueueAsync(new PreviewWorkItem { Data = "soon" }, new QueueMessageOptions { Delay = TimeSpan.FromMinutes(5) }, cancellationToken);

        Assert.Equal(1, nativeTransport.SendCount);
        Assert.NotNull(nativeTransport.LastSendOptions?.DeliverAt);
        Assert.Equal(0, await nativeProcessor.RunDueOccurrencesAsync(DateTimeOffset.UtcNow.AddYears(1), cancellationToken: cancellationToken));

        // Beyond the transport's maximum: routed through the runtime store instead of being silently truncated.
        var fallbackStore = new InMemoryJobRuntimeStore();
        await using var fallbackTransport = new CappedDelayTransport(maxDeliveryDelay: TimeSpan.FromMinutes(15));
        await using var fallbackQueue = new MessageQueue(fallbackTransport, new QueueOptions { RuntimeStore = fallbackStore });
        var fallbackProcessor = CreateDispatchProcessor(fallbackStore, fallbackTransport);

        await fallbackQueue.EnqueueAsync(new PreviewWorkItem { Data = "later" }, new QueueMessageOptions { Delay = TimeSpan.FromHours(1) }, cancellationToken);

        Assert.Equal(0, fallbackTransport.SendCount);
        Assert.Equal(1, await fallbackProcessor.RunDueOccurrencesAsync(DateTimeOffset.UtcNow.AddHours(2), cancellationToken: cancellationToken));
        Assert.Equal(1, fallbackTransport.SendCount);

        var delayed = await fallbackQueue.ReceiveAsync<PreviewWorkItem>(new QueueReceiveOptions { MaxWaitTime = TimeSpan.FromSeconds(2) }, cancellationToken);
        Assert.NotNull(delayed);
        Assert.Equal("later", delayed.Message.Data);
        await delayed.CompleteAsync(cancellationToken);
    }

    [Fact]
    public async Task StartConsumerAsync_WithRedeliveryBackoff_SchedulesRetryThroughRuntimeStoreAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new InMemoryJobRuntimeStore();
        // BasicQueueTransport lacks native redelivery delay, so the backoff routes through the runtime store. It also
        // never seeds the delivery count from the message.attempts header, proving the core reconciles the attempt
        // count from the header itself (second attempt must observe Attempts == 2, not a reset-to-1 loop).
        await using var transport = new BasicQueueTransport();
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

    [Fact]
    public async Task StartConsumerAsync_MultipleTypesOnOneDestination_DispatchByTypeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var queue = new MessageQueue(new InMemoryMessageTransport());
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        var aReceived = new List<string?>();
        var bReceived = new List<string?>();
        var aSignal = new AsyncCountdownEvent(1);
        var bSignal = new AsyncCountdownEvent(1);

        await using var consumerA = await queue.StartConsumerAsync<SharedAWorkItem>((message, _) =>
        {
            lock (aReceived)
                aReceived.Add(message.Message.Data);
            aSignal.Signal();
            return Task.CompletedTask;
        }, cancellationToken: cts.Token);

        await using var consumerB = await queue.StartConsumerAsync<SharedBWorkItem>((message, _) =>
        {
            lock (bReceived)
                bReceived.Add(message.Message.Data);
            bSignal.Signal();
            return Task.CompletedTask;
        }, cancellationToken: cts.Token);

        // Both types route to the same destination, so they share one underlying receive loop that dispatches by type.
        Assert.Equal(consumerA.Source, consumerB.Source);

        await queue.EnqueueAsync(new SharedAWorkItem { Data = "a" }, cancellationToken: cts.Token);
        await queue.EnqueueAsync(new SharedBWorkItem { Data = "b" }, cancellationToken: cts.Token);

        await aSignal.WaitAsync(TimeSpan.FromSeconds(2));
        await bSignal.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(new[] { "a" }, aReceived);
        Assert.Equal(new[] { "b" }, bReceived);
    }

    [Fact]
    public async Task StartConsumerAsync_UnmatchedType_DeadLettersAndKeepsConsumingAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var transport = new InMemoryMessageTransport();
        await using var queue = new MessageQueue(transport, new QueueOptions { RetryPolicy = new RetryPolicy { UnmatchedMaxAttempts = 3 } });
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(20));

        var aSignal = new AsyncCountdownEvent(1);
        await using var consumerA = await queue.StartConsumerAsync<SharedAWorkItem>((_, _) =>
        {
            aSignal.Signal();
            return Task.CompletedTask;
        }, cancellationToken: cts.Token);

        // SharedBWorkItem routes to the same destination but has no registered consumer on this node.
        await queue.EnqueueAsync(new SharedBWorkItem { Data = "orphan" }, cancellationToken: cts.Token);

        // It is retried and finally dead-lettered as "no-handler" once the configured unmatched budget is exhausted.
        for (int i = 0; i < 400; i++)
        {
            if ((await transport.GetStatsAsync("shared-demux", cts.Token)).Deadletter == 1)
                break;
            await Task.Delay(TimeSpan.FromMilliseconds(25), cts.Token);
        }

        Assert.Equal(1, (await transport.GetStatsAsync("shared-demux", cts.Token)).Deadletter);

        // The loop survived the unmatched message and keeps consuming the type it does handle.
        await queue.EnqueueAsync(new SharedAWorkItem { Data = "ok" }, cancellationToken: cts.Token);
        await aSignal.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task RejectAsync_Terminal_WithoutNativeDeadLetter_SendsToConfiguredDestinationAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var transport = new NoDeadLetterTransport();
        await using var queue = new MessageQueue(transport, new QueueOptions { RetryPolicy = new RetryPolicy { DeadLetterDestination = "preview-dead-letter" } });

        await queue.EnqueueAsync(new PreviewWorkItem { Data = "bad" }, cancellationToken: cancellationToken);
        var message = await queue.ReceiveAsync<PreviewWorkItem>(new QueueReceiveOptions { MaxWaitTime = TimeSpan.FromSeconds(1) }, cancellationToken);
        Assert.NotNull(message);

        await message.RejectAsync(new RejectOptions { Terminal = true, Reason = "validation" }, cancellationToken);

        // The transport has no native dead-letter sink, so core routes the terminal message to the configured destination.
        var dead = await queue.ReceiveAsync(new QueueReceiveOptions { Source = "preview-dead-letter", MaxWaitTime = TimeSpan.FromSeconds(1) }, cancellationToken);
        Assert.NotNull(dead);
        Assert.Equal("validation", dead.Headers.GetValueOrDefault(KnownHeaders.DeadLetterReason));
    }

    [Fact]
    public async Task StartConsumerAsync_UsesDefaultRetryPolicyMaxAttempts_WhenConsumerDoesNotOverrideAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var transport = new InMemoryMessageTransport();
        await using var queue = new MessageQueue(transport, new QueueOptions { RetryPolicy = new RetryPolicy { MaxAttempts = 2 } });
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(20));

        int attempts = 0;
        await using var consumer = await queue.StartConsumerAsync<PreviewWorkItem>((_, _) =>
        {
            Interlocked.Increment(ref attempts);
            throw new InvalidOperationException("always fails");
        }, cancellationToken: cts.Token); // no per-consumer MaxAttempts -> default RetryPolicy (2)

        await queue.EnqueueAsync(new PreviewWorkItem { Data = "x" }, cancellationToken: cts.Token);

        for (int i = 0; i < 400; i++)
        {
            if ((await transport.GetStatsAsync("preview-work-item", cts.Token)).Deadletter == 1)
                break;
            await Task.Delay(TimeSpan.FromMilliseconds(25), cts.Token);
        }

        Assert.Equal(1, (await transport.GetStatsAsync("preview-work-item", cts.Token)).Deadletter);
        Assert.Equal(2, attempts);
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

    [MessageRoute("shared-demux")]
    private sealed class SharedAWorkItem
    {
        public string? Data { get; set; }
    }

    [MessageRoute("shared-demux")]
    private sealed class SharedBWorkItem
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

    private sealed class BatchLimitTransport : IMessageTransport, ITransportInfo
    {
        public BatchLimitTransport(int maxBatchSize)
        {
            MaxBatchSize = maxBatchSize;
        }

        public List<int> SendBatchSizes { get; } = new();
        public DeliveryGuarantee DeliveryGuarantee => DeliveryGuarantee.AtLeastOnce;
        public OrderingGuarantee Ordering => OrderingGuarantee.Fifo;
        public IReadOnlySet<DestinationRole> SupportedRoles => new HashSet<DestinationRole> { DestinationRole.Queue };
        public int? MaxBatchSize { get; }
        public long? MaxMessageBytes => null;

        public Task<SendResult> SendAsync(string destination, IReadOnlyList<TransportMessage> messages, TransportSendOptions options, CancellationToken ct = default)
        {
            SendBatchSizes.Add(messages.Count);
            var items = new SendItemResult[messages.Count];
            for (int i = 0; i < messages.Count; i++)
                items[i] = new SendItemResult { MessageId = messages[i].MessageId ?? Guid.NewGuid().ToString("N"), Success = true };

            return Task.FromResult(new SendResult { Items = items });
        }

        public Task CompleteAsync(TransportEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task AbandonAsync(TransportEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CappedDelayTransport : IMessageTransport, ISupportsPull, ISupportsDelayedDelivery
    {
        private readonly Queue<TransportEntry> _entries = new();

        public CappedDelayTransport(TimeSpan? maxDeliveryDelay)
        {
            MaxDeliveryDelay = maxDeliveryDelay;
        }

        public TimeSpan? MaxDeliveryDelay { get; }
        public int SendCount { get; private set; }
        public TransportSendOptions? LastSendOptions { get; private set; }

        public Task<SendResult> SendAsync(string destination, IReadOnlyList<TransportMessage> messages, TransportSendOptions options, CancellationToken ct = default)
        {
            SendCount += messages.Count;
            LastSendOptions = options;
            var items = new SendItemResult[messages.Count];
            for (int i = 0; i < messages.Count; i++)
            {
                string id = messages[i].MessageId ?? Guid.NewGuid().ToString("N");
                _entries.Enqueue(new TransportEntry { Id = id, Destination = destination, Body = messages[i].Body, Headers = messages[i].Headers, Receipt = new Receipt() });
                items[i] = new SendItemResult { MessageId = id, Success = true };
            }

            return Task.FromResult(new SendResult { Items = items });
        }

        public Task<IReadOnlyList<TransportEntry>> ReceiveAsync(string source, ReceiveRequest request, CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyList<TransportEntry>>(_entries.Count > 0 ? [_entries.Dequeue()] : []);
        }

        public Task CompleteAsync(TransportEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task AbandonAsync(TransportEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // A minimal multi-destination pull transport with NO native dead-letter sink, used to prove core-managed
    // dead-lettering routes terminal messages to the configured RetryPolicy.DeadLetterDestination.
    private sealed class NoDeadLetterTransport : IMessageTransport, ISupportsPull
    {
        private readonly ConcurrentDictionary<string, ConcurrentQueue<TransportEntry>> _queues = new(StringComparer.Ordinal);

        public Task<SendResult> SendAsync(string destination, IReadOnlyList<TransportMessage> messages, TransportSendOptions options, CancellationToken ct = default)
        {
            var queue = _queues.GetOrAdd(destination, _ => new ConcurrentQueue<TransportEntry>());
            var items = new SendItemResult[messages.Count];
            for (int i = 0; i < messages.Count; i++)
            {
                string id = messages[i].MessageId ?? Guid.NewGuid().ToString("N");
                queue.Enqueue(new TransportEntry { Id = id, Destination = destination, Body = messages[i].Body, Headers = messages[i].Headers, Receipt = new Receipt() });
                items[i] = new SendItemResult { MessageId = id, Success = true };
            }

            return Task.FromResult(new SendResult { Items = items });
        }

        public Task<IReadOnlyList<TransportEntry>> ReceiveAsync(string source, ReceiveRequest request, CancellationToken ct)
        {
            if (_queues.TryGetValue(source, out var queue) && queue.TryDequeue(out var entry))
                return Task.FromResult<IReadOnlyList<TransportEntry>>([entry]);

            return Task.FromResult<IReadOnlyList<TransportEntry>>([]);
        }

        public Task CompleteAsync(TransportEntry entry, CancellationToken ct = default) => Task.CompletedTask;

        public Task AbandonAsync(TransportEntry entry, CancellationToken ct = default)
        {
            _queues.GetOrAdd(entry.Destination, _ => new ConcurrentQueue<TransportEntry>()).Enqueue(entry with { DeliveryCount = entry.DeliveryCount + 1 });
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}