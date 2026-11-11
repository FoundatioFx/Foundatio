using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;
using Foundatio.Xunit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Foundatio.Tests.Messaging;

public class MessagingTests : TestWithLoggingBase
{
    private readonly InMemoryMessageTransport _transport = new();

    public MessagingTests(ITestOutputHelper output) : base(output)
    {
    }

    [Fact]
    public async Task HandlerClass_IsResolvedFromANewScopePerMessage()
    {
        var recorder = new Recorder();
        await using var host = await StartAsync(m => m.AddConsumer<PlaceOrderHandler>(), s => s.AddSingleton(recorder).AddScoped<ScopedDependency>());

        await host.Client.SendAsync(new PlaceOrder(1), cancellationToken: TestCancellationToken);
        await host.Client.SendAsync(new PlaceOrder(2), cancellationToken: TestCancellationToken);

        await recorder.WaitForAsync(2);
        Assert.Equal([1, 2], recorder.Values.Order());
        Assert.Equal(2, recorder.Scopes.Distinct().Count());
    }

    [Fact]
    public async Task Shutdown_LetsInFlightMessagesFinishAndCompletesThem()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int handled = 0;
        var host = await StartAsync(m => m.AddConsumer<PlaceOrder>(async (_, _, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(500, ct);
            Interlocked.Increment(ref handled);
        }));

        await host.Client.SendAsync(new PlaceOrder(1), cancellationToken: TestCancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestCancellationToken);
        await host.DisposeAsync();

        Assert.Equal(1, handled);
        Assert.Equal(0, _transport.GetMessageCount(MessageSource.Queue(QueueName<PlaceOrder>())));
    }

    [Fact]
    public async Task Shutdown_CancelsHandlersThatOutliveTheTimeoutAndRedeliversTheirMessages()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = await StartAsync(m => m
            .Configure(o => o.ShutdownTimeout = TimeSpan.FromMilliseconds(200))
            .AddConsumer<PlaceOrder>(async (_, _, ct) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }));

        await host.Client.SendAsync(new PlaceOrder(1), cancellationToken: TestCancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestCancellationToken);
        await host.DisposeAsync();

        var redelivered = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var next = await StartAsync(m => m.AddConsumer<PlaceOrder>((_, context, _) => { redelivered.TrySetResult(context.Attempt); return Task.CompletedTask; }));
        Assert.Equal(2, await redelivered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestCancellationToken));
    }

    [Fact]
    public async Task LongRunningHandler_KeepsItsLeaseAndIsNotRedelivered()
    {
        int handled = 0;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await StartAsync(m => m.AddConsumer<PlaceOrder>(async (_, _, ct) =>
        {
            Interlocked.Increment(ref handled);
            await Task.Delay(TimeSpan.FromSeconds(2.5), ct);
            done.TrySetResult();
        }, new HandlerOptions { LeaseDuration = TimeSpan.FromSeconds(1), MaxConcurrency = 4 }));

        await host.Client.SendAsync(new PlaceOrder(1), cancellationToken: TestCancellationToken);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10), TestCancellationToken);
        await Task.Delay(1500, TestCancellationToken);

        Assert.Equal(1, handled);
    }

    [Fact]
    public async Task MaxConcurrency_LimitsHowManyMessagesRunAtOnce()
    {
        int running = 0, peak = 0, handled = 0;
        await using var host = await StartAsync(m => m.AddConsumer<PlaceOrder>(async (_, _, ct) =>
        {
            int now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            await Task.Delay(50, ct);
            Interlocked.Decrement(ref running);
            Interlocked.Increment(ref handled);
        }, new HandlerOptions { MaxConcurrency = 3 }));

        await host.Client.SendBatchAsync(Enumerable.Range(0, 20).Select(i => new PlaceOrder(i)), cancellationToken: TestCancellationToken);
        await WaitForAsync(() => Volatile.Read(ref handled) == 20);

        Assert.Equal(3, peak);
    }

    [Fact]
    public async Task UnhandledTypeOnASharedTopic_IsSkippedWithoutRetries()
    {
        var received = new ConcurrentBag<string>();
        await using var host = await StartAsync(m => m
            .MapMessage<OrderShipped>("orders.shipped", topic: "orders")
            .MapMessage<OrderCancelled>("orders.cancelled", topic: "orders")
            .AddSubscriber<OrderShipped>((message, _, _) => { received.Add($"shipped {message.OrderId}"); return Task.CompletedTask; }));

        await host.Client.PublishAsync(new OrderCancelled(1), cancellationToken: TestCancellationToken);
        await host.Client.PublishAsync(new OrderShipped(2), cancellationToken: TestCancellationToken);

        Assert.True(await _transport.WaitUntilIdleAsync(TimeSpan.FromSeconds(5), TestCancellationToken));
        Assert.Equal(["shipped 2"], received);
        Assert.Equal(0, _transport.GetMessageCount(MessageSource.DeadLetter(MessageSource.Subscription("orders", "tests"))));
    }

    [Fact]
    public async Task UnhandledTypeOnAQueue_IsRetriedThenDeadLettered()
    {
        await using var producer = await StartAsync(m => m.MapMessage<OrderShipped>("orders.event", queue: "orders"));
        await using var consumer = await StartAsync(m => m
            .Configure(o => o.DefaultRetryPolicy = new RetryPolicy { MaxAttempts = 2, Delay = RetryPolicy.Fixed(TimeSpan.FromMilliseconds(50)) })
            .MapMessage<OrderCancelled>("orders.cancelled", queue: "orders")
            .AddConsumer<OrderCancelled>((_, _, _) => Task.CompletedTask));

        await producer.Client.SendAsync(new OrderShipped(1), cancellationToken: TestCancellationToken);

        await WaitForAsync(() => _transport.GetMessageCount(MessageSource.DeadLetter(MessageSource.Queue("orders"))) == 1);
    }

    [Fact]
    public async Task Publish_RoutesByRuntimeType_AndBaseTypeHandlersReceiveMappedDerivedTypes()
    {
        var received = new ConcurrentBag<string>();
        await using var host = await StartAsync(m => m
            .MapMessage<OrderEvent>("order-events", topic: "order-events")
            .MapMessage<OrderShipped>("order-shipped", topic: "order-events")
            .AddSubscriber<OrderEvent>((message, context, _) => { received.Add($"{context.MessageType}:{message.OrderId}"); return Task.CompletedTask; }));

        OrderEvent shipped = new OrderShipped(7);
        await host.Client.PublishAsync(shipped, cancellationToken: TestCancellationToken);

        await WaitForAsync(() => received.Count == 1);
        Assert.Equal("order-shipped:7", received.Single());
    }

    [Fact]
    public async Task Context_DeadLetterAndRetry_DecideTheOutcomeWithoutThrowing()
    {
        var attempts = new ConcurrentBag<int>();
        await using var host = await StartAsync(m => m.AddConsumer<PlaceOrder>((message, context, _) =>
        {
            attempts.Add(context.Attempt);
            if (message.OrderId < 0)
                context.DeadLetter("negative order id");
            else if (context.Attempt == 1)
                context.Retry(TimeSpan.FromMilliseconds(50));

            return Task.CompletedTask;
        }));

        await host.Client.SendAsync(new PlaceOrder(-1), cancellationToken: TestCancellationToken);
        await host.Client.SendAsync(new PlaceOrder(1), cancellationToken: TestCancellationToken);

        Assert.True(await _transport.WaitUntilIdleAsync(TimeSpan.FromSeconds(5), TestCancellationToken));
        Assert.Equal([1, 1, 2], attempts.Order());
        var deadLetters = await ReceiveDeadLettersAsync(MessageSource.Queue(QueueName<PlaceOrder>()));
        Assert.Equal("negative order id", Assert.Single(deadLetters).Headers[KnownHeaders.DeadLetterReason]);
    }

    [Fact]
    public async Task DoNotRetry_DeadLettersOnTheFirstFailure()
    {
        int attempts = 0;
        await using var host = await StartAsync(m => m.AddConsumer<PlaceOrder>((_, _, _) =>
        {
            Interlocked.Increment(ref attempts);
            throw new ArgumentException("invalid");
        }, new HandlerOptions { RetryPolicy = RetryPolicy.Default.DoNotRetry<ArgumentException>() }));

        await host.Client.SendAsync(new PlaceOrder(1), cancellationToken: TestCancellationToken);

        await WaitForAsync(() => _transport.GetMessageCount(MessageSource.DeadLetter(MessageSource.Queue(QueueName<PlaceOrder>()))) == 1);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task MessagesSentWhileHandling_InheritTheCorrelationId()
    {
        var correlation = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await StartAsync(m => m
            .AddSubscriber<OrderShipped>((_, context, _) => { correlation.TrySetResult(context.CorrelationId); return Task.CompletedTask; })
            .AddConsumer<ShipOrder>(async (order, context, ct) =>
            {
                Assert.Equal("request-42", context.CorrelationId);
                await ServicesClient!.PublishAsync(new OrderShipped(order.OrderId), cancellationToken: ct);
            }));
        ServicesClient = host.Client;

        await host.Client.SendAsync(new ShipOrder(1), new SendOptions { CorrelationId = "request-42" }, TestCancellationToken);

        Assert.Equal("request-42", await correlation.Task.WaitAsync(TimeSpan.FromSeconds(5), TestCancellationToken));
    }

    [Fact]
    public async Task ExpiredMessages_AreDiscarded()
    {
        int handled = 0;
        await using var producer = await StartAsync(_ => { });
        await producer.Client.SendAsync(new PlaceOrder(1), new SendOptions { TimeToLive = TimeSpan.FromMilliseconds(50) }, TestCancellationToken);
        await Task.Delay(200, TestCancellationToken);

        await using var consumer = await StartAsync(m => m.AddConsumer<PlaceOrder>((_, _, _) => { Interlocked.Increment(ref handled); return Task.CompletedTask; }));

        Assert.True(await _transport.WaitUntilIdleAsync(TimeSpan.FromSeconds(5), TestCancellationToken));
        Assert.Equal(0, handled);
    }

    [Fact]
    public async Task DelaysBeyondTheTransportLimit_AreEmulatedForSends_AndRejectedForPublishes()
    {
        var limited = new LimitedTransport(_transport, _transport.Capabilities with { MaxDelay = TimeSpan.FromMilliseconds(500) });
        var handledAfter = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopwatch = Stopwatch.StartNew();
        await using var host = await MessagingTestHost.StartAsync(limited, Log, "tests", m => m
            .AddConsumer<PlaceOrder>((_, context, _) => { handledAfter.TrySetResult(stopwatch.Elapsed); Assert.Equal(1, context.Attempt); return Task.CompletedTask; }));

        await host.Client.SendAsync(new PlaceOrder(1), new SendOptions { Delay = TimeSpan.FromSeconds(1.6) }, TestCancellationToken);

        var elapsed = await handledAfter.Task.WaitAsync(TimeSpan.FromSeconds(10), TestCancellationToken);
        Assert.InRange(elapsed, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<MessageBusException>(() => host.Client.PublishAsync(new OrderShipped(1), new PublishOptions { Delay = TimeSpan.FromSeconds(2) }, TestCancellationToken));
    }

    [Fact]
    public async Task RuntimeSubscription_ReceivesEventsUntilDisposed()
    {
        await using var host = await StartAsync(_ => { });
        var received = new ConcurrentBag<int>();
        var subscription = await host.Client.SubscribeAsync<OrderShipped>((message, _, _) => { received.Add(message.OrderId); return Task.CompletedTask; }, cancellationToken: TestCancellationToken);

        await host.Client.PublishAsync(new OrderShipped(1), cancellationToken: TestCancellationToken);
        await WaitForAsync(() => received.Count == 1);
        await subscription.DisposeAsync();
        await host.Client.PublishAsync(new OrderShipped(2), cancellationToken: TestCancellationToken);
        await Task.Delay(200, TestCancellationToken);

        Assert.Equal([1], received);
    }

    [Fact]
    public void Registration_RejectsMistakesEarlyWithClearErrors()
    {
        var services = new ServiceCollection();
        services.AddFoundatioMessaging(m => m.AddConsumer<PlaceOrder>((_, _, _) => Task.CompletedTask));

        var duplicate = Assert.Throws<ArgumentException>(() => services.AddFoundatioMessaging(m => m.AddConsumer<PlaceOrderHandler>()));
        Assert.Contains("already has a consumer", duplicate.Message);
        Assert.Throws<ArgumentException>(() => services.AddFoundatioMessaging(m => m.AddConsumer<Recorder>()));
        Assert.Throws<ArgumentException>(() => services.AddFoundatioMessaging(m => m.MapMessage<PlaceOrder>("not valid!")));
        Assert.Throws<ArgumentOutOfRangeException>(() => services.AddFoundatioMessaging(m => m.AddSubscriber<OrderShipped>((_, _, _) => Task.CompletedTask, new SubscriptionOptions { MaxConcurrency = 0 })));

        var noTransport = Assert.Throws<InvalidOperationException>(() => services.BuildServiceProvider().GetRequiredService<IMessageClient>());
        Assert.Contains("UseInMemory()", noTransport.Message);
    }

    [Fact]
    public async Task ConflictingMessageNames_AreReported()
    {
        await using var host = await StartAsync(m => m.MapMessage<OrderShipped>("orders.same"));

        var exception = await Assert.ThrowsAsync<MessageBusException>(() => host.Client.SendAsync(new SameNameMessage(), cancellationToken: TestCancellationToken));
        Assert.Contains("both use the name \"orders.same\"", exception.Message);
    }

    [Fact]
    public async Task RegistrationsFromSeveralCalls_AllRun()
    {
        var received = new ConcurrentBag<string>();
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(Log);
        services.AddFoundatioMessaging(m => m.UseTransport(_transport).UseServiceName("tests"));
        services.AddFoundatioMessaging(m => m.AddConsumer<PlaceOrder>((_, _, _) => { received.Add("consumer"); return Task.CompletedTask; }));
        services.AddFoundatioMessaging(m => m.AddSubscriber<OrderShipped>((_, _, _) => { received.Add("subscriber"); return Task.CompletedTask; }));
        await using var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>().ToList();
        Assert.Single(hosted);
        await hosted[0].StartAsync(TestCancellationToken);

        var client = provider.GetRequiredService<IMessageClient>();
        await client.SendAsync(new PlaceOrder(1), cancellationToken: TestCancellationToken);
        await client.PublishAsync(new OrderShipped(1), cancellationToken: TestCancellationToken);

        await WaitForAsync(() => received.Count == 2);
        await hosted[0].StopAsync(TestCancellationToken);
    }

    [Fact]
    public async Task MessagesOverTheTransportSizeLimit_AreRejectedBeforeSending()
    {
        var limited = new LimitedTransport(_transport, _transport.Capabilities with { MaxMessageSize = 100 });
        await using var host = await MessagingTestHost.StartAsync(limited, Log, "tests", _ => { });

        var ex = await Assert.ThrowsAsync<MessageTooLargeException>(() => host.Client.SendAsync(new Note(new string('x', 200)), cancellationToken: TestCancellationToken));
        Assert.Equal(100, ex.Limit);
    }

    [Fact]
    public async Task SendOptions_WithIdsLongerThanTheLimit_AreRejected()
    {
        await using var host = await StartAsync(_ => { });
        string tooLong = new('k', OutgoingMessage.MaxIdLength + 1);

        await Assert.ThrowsAsync<ArgumentException>(() => host.Client.SendAsync(new PlaceOrder(1), new SendOptions { GroupKey = tooLong }, TestCancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => host.Client.SendAsync(new PlaceOrder(1), new SendOptions { DeduplicationId = tooLong }, TestCancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => host.Client.PublishAsync(new OrderShipped(1), new PublishOptions { MessageId = tooLong }, TestCancellationToken));
        await host.Client.SendAsync(new PlaceOrder(1), new SendOptions { GroupKey = new string('k', OutgoingMessage.MaxIdLength) }, TestCancellationToken);
    }

    [Fact]
    public async Task PublishDelays_UseTheTransportsPublishLimit()
    {
        var unlimitedPublishes = new LimitedTransport(_transport, _transport.Capabilities with { MaxDelay = TimeSpan.FromMilliseconds(200), MaxPublishDelay = null });
        var handledAfter = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopwatch = Stopwatch.StartNew();
        await using (var host = await MessagingTestHost.StartAsync(unlimitedPublishes, Log, "tests", m => m
            .AddSubscriber<OrderShipped>((_, _, _) => { handledAfter.TrySetResult(stopwatch.Elapsed); return Task.CompletedTask; })))
        {
            await host.Client.PublishAsync(new OrderShipped(1), new PublishOptions { Delay = TimeSpan.FromSeconds(1) }, TestCancellationToken);
            Assert.InRange(await handledAfter.Task.WaitAsync(TimeSpan.FromSeconds(10), TestCancellationToken), TimeSpan.FromSeconds(0.9), TimeSpan.FromSeconds(5));
        }

        var noPublishDelays = new LimitedTransport(_transport, _transport.Capabilities with { MaxPublishDelay = TimeSpan.Zero });
        await using var other = await MessagingTestHost.StartAsync(noPublishDelays, Log, "tests", _ => { });
        var rejected = await Assert.ThrowsAsync<MessageBusException>(() => other.Client.PublishAsync(new OrderShipped(2), new PublishOptions { Delay = TimeSpan.FromSeconds(1) }, TestCancellationToken));
        Assert.Contains("does not support delayed publishes", rejected.Message);
        await other.Client.SendAsync(new PlaceOrder(2), new SendOptions { Delay = TimeSpan.FromSeconds(1) }, TestCancellationToken);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(200)]
    public async Task RetryDelays_RespectTheTransportsRetryLimit(int maxRetryDelayMs)
    {
        var limited = new LimitedTransport(_transport, _transport.Capabilities with { MaxRetryDelay = TimeSpan.FromMilliseconds(maxRetryDelayMs) });
        var attempts = new ConcurrentQueue<TimeSpan>();
        var stopwatch = Stopwatch.StartNew();
        var retryDelay = maxRetryDelayMs == 0 ? TimeSpan.FromMilliseconds(400) : TimeSpan.FromSeconds(30);
        await using var host = await MessagingTestHost.StartAsync(limited, Log, "tests", m => m.AddConsumer<PlaceOrder>((_, context, _) =>
        {
            attempts.Enqueue(stopwatch.Elapsed);
            if (context.Attempt == 1)
                context.Retry(retryDelay);

            return Task.CompletedTask;
        }));

        await host.Client.SendAsync(new PlaceOrder(1), cancellationToken: TestCancellationToken);
        await WaitForAsync(() => attempts.Count == 2);

        var gap = attempts.Last() - attempts.First();
        if (maxRetryDelayMs == 0)
            Assert.True(gap >= TimeSpan.FromMilliseconds(350), $"Without retry delays the backoff is held in-process; retried after {gap}.");
        else
            Assert.InRange(gap, TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task OrderedGroups_AreHandledInOrder_AndLaterMessagesWaitForARetry()
    {
        var calls = new ConcurrentQueue<(int Value, int Attempt)>();
        await using var host = await StartAsync(m => m.AddConsumer<PlaceOrder>((message, context, _) =>
        {
            calls.Enqueue((message.OrderId, context.Attempt));
            if (message.OrderId == 3 && context.Attempt == 1)
                throw new InvalidOperationException("transient");

            return Task.CompletedTask;
        }, new HandlerOptions { MaxConcurrency = 8, RetryPolicy = new RetryPolicy { Delay = RetryPolicy.Fixed(TimeSpan.FromMilliseconds(100)) } }));

        await host.Client.SendBatchAsync(Enumerable.Range(1, 5).Select(i => new PlaceOrder(i)), new SendOptions { GroupKey = "customer-1" }, TestCancellationToken);

        await WaitForAsync(() => calls.Count == 6);
        Assert.Equal([1, 2, 3, 3, 4, 5], calls.Select(c => c.Value));
        Assert.Equal(2, calls.Where(c => c.Value == 3).Last().Attempt);
    }

    [Fact]
    public async Task Admin_PeeksReplaysPurgesAndDeletesDeadLetters()
    {
        int calls = 0;
        await using var host = await StartAsync(m => m.AddConsumer<PlaceOrder>((message, _, _) =>
        {
            Interlocked.Increment(ref calls);
            if (message.OrderId < 10)
                throw new InvalidOperationException($"order {message.OrderId} is invalid");

            return Task.CompletedTask;
        }, new HandlerOptions { RetryPolicy = RetryPolicy.None }));
        var admin = host.Services.GetRequiredService<IMessageAdmin>();
        var source = MessageSource.Queue(QueueName<PlaceOrder>());

        await host.Client.SendAsync(new PlaceOrder(1), new SendOptions { CorrelationId = "c-1" }, TestCancellationToken);
        await host.Client.SendAsync(new PlaceOrder(2), cancellationToken: TestCancellationToken);
        await WaitForAsync(() => _transport.GetMessageCount(MessageSource.DeadLetter(source)) == 2);

        var peeked = await admin.PeekDeadLettersAsync(source, cancellationToken: TestCancellationToken);
        Assert.Equal(2, peeked.Count);
        var first = Assert.Single(peeked, d => d.CorrelationId == "c-1");
        Assert.Equal(new PlaceOrder(1), first.Message);
        Assert.Contains("order 1 is invalid", first.Reason);
        Assert.Equal(QueueName<PlaceOrder>(), first.MessageType);
        Assert.Equal(2, (await admin.PeekDeadLettersAsync(source, cancellationToken: TestCancellationToken)).Count);

        Assert.Equal(1, await admin.ReplayDeadLettersAsync(source, max: 1, cancellationToken: TestCancellationToken));
        await WaitForAsync(() => calls == 3 && _transport.GetMessageCount(MessageSource.DeadLetter(source)) == 2);

        await admin.PurgeDeadLettersAsync(source, TestCancellationToken);
        Assert.Empty(await admin.PeekDeadLettersAsync(source, cancellationToken: TestCancellationToken));

        await admin.DeleteAsync(MessageSource.Subscription("unused.topic", "removed-service"), TestCancellationToken);
        await Assert.ThrowsAsync<ArgumentException>(() => admin.PeekDeadLettersAsync(MessageSource.InstanceSubscription("unused.topic"), cancellationToken: TestCancellationToken));
    }

    public override async ValueTask DisposeAsync()
    {
        await _transport.DisposeAsync();
        await base.DisposeAsync();
    }

    private IMessageClient? ServicesClient { get; set; }

    private Task<MessagingTestHost> StartAsync(Action<FoundatioMessagingBuilder> configure, Action<IServiceCollection>? services = null)
    {
        return MessagingTestHost.StartAsync(_transport, Log, "tests", configure, services);
    }

    private async Task<IReadOnlyList<IncomingMessage>> ReceiveDeadLettersAsync(MessageSource source)
    {
        await using var receiver = await _transport.CreateReceiverAsync(MessageSource.DeadLetter(source), new ReceiverOptions(), TestCancellationToken);
        return await receiver.ReceiveAsync(10, TimeSpan.FromMilliseconds(200), TestCancellationToken);
    }

    private static string QueueName<T>() => typeof(T).FullName!.Replace('+', '.');

    private async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The condition was not met in time.");

            await Task.Delay(20, TestCancellationToken);
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    public record PlaceOrder(int OrderId);
    public record ShipOrder(int OrderId);
    public record OrderEvent(int OrderId);
    public record OrderShipped(int OrderId) : OrderEvent(OrderId);
    public record OrderCancelled(int OrderId) : OrderEvent(OrderId);
    public record Note(string Text);

    [Message("orders.same")]
    public record SameNameMessage;

    public sealed class ScopedDependency
    {
        public Guid Id { get; } = Guid.NewGuid();
    }

    public sealed class Recorder
    {
        public ConcurrentBag<int> Values { get; } = [];
        public ConcurrentBag<Guid> Scopes { get; } = [];

        public async Task WaitForAsync(int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (Values.Count < count && DateTime.UtcNow < deadline)
                await Task.Delay(20);

            Assert.Equal(count, Values.Count);
        }
    }

    public sealed class PlaceOrderHandler(Recorder recorder, ScopedDependency dependency) : IMessageHandler<PlaceOrder>
    {
        public Task HandleAsync(PlaceOrder message, IMessageContext context, CancellationToken cancellationToken)
        {
            recorder.Values.Add(message.OrderId);
            recorder.Scopes.Add(dependency.Id);
            return Task.CompletedTask;
        }
    }

    /// <summary>Wraps a transport and advertises smaller limits, to exercise Foundatio's emulation.</summary>
    private sealed class LimitedTransport(IMessageTransport inner, TransportCapabilities capabilities) : IMessageTransport
    {
        public TransportCapabilities Capabilities { get; } = capabilities;

        public Task SendAsync(MessageDestination destination, IReadOnlyList<OutgoingMessage> messages, CancellationToken cancellationToken = default)
        {
            var limit = destination.Kind == MessageDestinationKind.Topic ? Capabilities.MaxPublishDelay : Capabilities.MaxDelay;
            if (limit is { } max && messages.Any(m => m.DeliverAt > DateTimeOffset.UtcNow.Add(max).AddMilliseconds(100)))
                throw new InvalidOperationException("Delay exceeds the advertised maximum.");

            return inner.SendAsync(destination, messages, cancellationToken);
        }

        public Task<IMessageReceiver> CreateReceiverAsync(MessageSource source, ReceiverOptions options, CancellationToken cancellationToken = default)
            => inner.CreateReceiverAsync(source, options, cancellationToken);

        public Task DeleteAsync(MessageSource source, CancellationToken cancellationToken = default) => inner.DeleteAsync(source, cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
