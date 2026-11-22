using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;
using Foundatio.Resilience;
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
    public async Task DelegateHandler_ReadingContextServices_GetsANewScopePerMessageDisposedAfterIt()
    {
        var scopes = new ConcurrentQueue<DisposableDependency>();
        var sentAt = new ConcurrentQueue<DateTimeOffset?>();
        int handled = 0;
        await using var host = await StartAsync(m => m.AddConsumer<PlaceOrder>((_, context, _) =>
        {
            if (context.MessageType.Length > 0)
                scopes.Enqueue(context.Services.GetRequiredService<DisposableDependency>());

            sentAt.Enqueue(context.SentAt);
            Interlocked.Increment(ref handled);
            return Task.CompletedTask;
        }), s => s.AddScoped<DisposableDependency>());

        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        await host.Client.SendAsync(new PlaceOrder(1), cancellationToken: TestCancellationToken);
        await host.Client.SendAsync(new PlaceOrder(2), cancellationToken: TestCancellationToken);
        Assert.True(await _transport.WaitUntilIdleAsync(TimeSpan.FromSeconds(10), TestCancellationToken));

        Assert.Equal(2, Volatile.Read(ref handled));
        Assert.Equal(2, scopes.Distinct().Count());
        Assert.All(scopes, dependency => Assert.True(dependency.Disposed));
        Assert.All(sentAt, time => Assert.True(time > before && time <= DateTimeOffset.UtcNow));
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
    public async Task SendBatchAsync_WithTypesSharingAQueue_DeliversEachWithItsOwnTypeAndId()
    {
        var received = new ConcurrentQueue<(string Type, string Id, int OrderId)>();
        await using var host = await StartAsync(m => m
            .MapMessage<OrderEvent>("orders.event", queue: "orders")
            .MapMessage<OrderShipped>("orders.shipped", queue: "orders")
            .MapMessage<OrderCancelled>("orders.cancelled", queue: "orders")
            .AddConsumer<OrderEvent>((message, context, _) =>
            {
                received.Enqueue((context.MessageType, context.MessageId, message.OrderId));
                return Task.CompletedTask;
            }));

        OrderEvent[] batch = [new OrderShipped(1), new OrderShipped(2), new OrderCancelled(3), new OrderShipped(4), new OrderEvent(5)];
        await host.Client.SendBatchAsync(batch, cancellationToken: TestCancellationToken);

        await WaitForAsync(() => received.Count == batch.Length);
        Assert.Equal(["orders.shipped:1", "orders.shipped:2", "orders.cancelled:3", "orders.shipped:4", "orders.event:5"],
            received.OrderBy(r => r.OrderId).Select(r => $"{r.Type}:{r.OrderId}"));
        Assert.Equal(batch.Length, received.Select(r => r.Id).Distinct().Count());
    }

    [Fact]
    public async Task SendAndPublish_ReturnTheMessageIdTheHandlerSees()
    {
        var received = new ConcurrentDictionary<int, string>();
        await using var host = await StartAsync(m => m
            .AddConsumer<PlaceOrder>((message, context, _) =>
            {
                received[message.OrderId] = context.MessageId;
                return Task.CompletedTask;
            })
            .AddSubscriber<OrderShipped>((message, context, _) =>
            {
                received[message.OrderId] = context.MessageId;
                return Task.CompletedTask;
            }));

        string sent = await host.Client.SendAsync(new PlaceOrder(1), cancellationToken: TestCancellationToken);
        string chosen = await host.Client.SendAsync(new PlaceOrder(2), new SendOptions { MessageId = "order-2" }, TestCancellationToken);
        string published = await host.Client.PublishAsync(new OrderShipped(3), cancellationToken: TestCancellationToken);

        await WaitForAsync(() => received.Count == 3);
        Assert.Equal(received[1], sent);
        Assert.Equal("order-2", chosen);
        Assert.Equal(received[2], chosen);
        Assert.Equal(received[3], published);
    }

    [Fact]
    public async Task SendBatchAsync_ReturnsTheIdsInOrder_EvenWhenTheMessagesGoToSeveralQueues()
    {
        var received = new ConcurrentDictionary<string, int>();
        await using var host = await StartAsync(m => m
            .AddConsumer<PlaceOrder>((message, context, _) =>
            {
                received[context.MessageId] = message.OrderId;
                return Task.CompletedTask;
            })
            .AddConsumer<ShipOrder>((message, context, _) =>
            {
                received[context.MessageId] = -message.OrderId;
                return Task.CompletedTask;
            }));

        object[] batch = [new PlaceOrder(1), new ShipOrder(2), new PlaceOrder(3), new ShipOrder(4)];
        var ids = await host.Client.SendBatchAsync(batch, cancellationToken: TestCancellationToken);
        var published = await host.Client.PublishBatchAsync([new OrderShipped(5), new OrderShipped(6)], cancellationToken: TestCancellationToken);

        await WaitForAsync(() => received.Count == 4);
        Assert.Equal([1, -2, 3, -4], ids.Select(id => received[id]));
        Assert.Equal(2, published.Distinct().Count());
        Assert.Empty(await host.Client.SendBatchAsync(Array.Empty<PlaceOrder>(), cancellationToken: TestCancellationToken));
    }

    [Fact]
    public async Task SendBatchAsync_WithPerMessageKeys_SendsEachWithItsOwnIdsSoARetriedBatchIsDeduplicated()
    {
        var received = new ConcurrentQueue<(int OrderId, string Id, string? GroupKey)>();
        await using var host = await StartAsync(m => m.AddConsumer<PlaceOrder>((message, context, _) =>
        {
            received.Enqueue((message.OrderId, context.MessageId, context.GroupKey));
            return Task.CompletedTask;
        }));

        PlaceOrder[] batch = [new PlaceOrder(1), new PlaceOrder(2), new PlaceOrder(3)];
        static MessageKeys Keys(PlaceOrder order) => new()
        {
            MessageId = $"order-{order.OrderId}",
            DeduplicationId = $"order-{order.OrderId}",
            GroupKey = order.OrderId == 3 ? null : $"account-{order.OrderId % 2}"
        };

        var ids = await host.Client.SendBatchAsync(batch, Keys, new SendOptions { GroupKey = "default" }, TestCancellationToken);
        var retried = await host.Client.SendBatchAsync(batch, Keys, cancellationToken: TestCancellationToken);

        Assert.Equal(["order-1", "order-2", "order-3"], ids);
        Assert.Equal(ids, retried);
        await WaitForAsync(() => received.Count == 3);
        await Task.Delay(100, TestCancellationToken);
        Assert.Equal([(1, "order-1", "account-1"), (2, "order-2", "account-0"), (3, "order-3", "default")], received.OrderBy(r => r.OrderId));

        await Assert.ThrowsAsync<ArgumentException>(() => host.Client.PublishBatchAsync([new OrderShipped(1)], _ => new MessageKeys { MessageId = new string('k', OutgoingMessage.MaxIdLength + 1) }, cancellationToken: TestCancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => host.Client.SendBatchAsync(batch, Keys, new SendOptions { MessageId = "shared" }, TestCancellationToken));
    }

    [Fact]
    public async Task Telemetry_TagsSpansAndMetricsWithTheOpenTelemetryMessagingAttributes()
    {
        string queue = $"tests.telemetry-{Guid.NewGuid():N}";
        string topic = $"tests.telemetry-event-{Guid.NewGuid():N}";
        var activities = new ConcurrentQueue<Activity>();
        var foundatio = FoundatioDiagnostics.ActivitySource;
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s == foundatio,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activities.Enqueue
        };
        ActivitySource.AddActivityListener(listener);
        var measurements = new ConcurrentQueue<(string Instrument, string? Unit, double Value, Dictionary<string, object?> Tags)>();
        using var meterListener = new System.Diagnostics.Metrics.MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == FoundatioDiagnostics.Meter.Name)
                    l.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => measurements.Enqueue((instrument.Name, instrument.Unit, value, ToDictionary(tags))));
        meterListener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => measurements.Enqueue((instrument.Name, instrument.Unit, value, ToDictionary(tags))));
        meterListener.Start();

        int handled = 0;
        await using var host = await StartAsync(m => m
            .MapMessage<PlaceOrder>(queue)
            .MapMessage<OrderShipped>(topic)
            .AddConsumer<PlaceOrder>((message, _, _) =>
            {
                Interlocked.Increment(ref handled);
                return message.OrderId == 2 ? throw new InvalidOperationException("boom") : Task.CompletedTask;
            }, new HandlerOptions { RetryPolicy = RetryPolicy.None })
            .AddSubscriber<OrderShipped>((_, _, _) => { Interlocked.Increment(ref handled); return Task.CompletedTask; }));

        string id = await host.Client.SendAsync(new PlaceOrder(1), new SendOptions { CorrelationId = "c-1" }, TestCancellationToken);
        await host.Client.SendAsync(new PlaceOrder(2), cancellationToken: TestCancellationToken);
        await host.Client.PublishAsync(new OrderShipped(3), cancellationToken: TestCancellationToken);
        await WaitForAsync(() => Volatile.Read(ref handled) == 3 && activities.Count(a => a.OperationName.EndsWith(queue) || a.OperationName.EndsWith(topic)) == 6);
        meterListener.RecordObservableInstruments();

        var send = Assert.Single(activities, a => a.OperationName == $"send {queue}" && (string?)a.GetTagItem("messaging.message.id") == id);
        Assert.Equal(ActivityKind.Producer, send.Kind);
        Assert.Equal(("inmemory", "send", "send", queue, "c-1", queue), ((string?)send.GetTagItem("messaging.system"), (string?)send.GetTagItem("messaging.operation.type"),
            (string?)send.GetTagItem("messaging.operation.name"), (string?)send.GetTagItem("messaging.destination.name"), (string?)send.GetTagItem("messaging.message.conversation_id"),
            (string?)send.GetTagItem("foundatio.message.type")));
        var process = Assert.Single(activities, a => a.OperationName == $"process {queue}" && (string?)a.GetTagItem("messaging.message.id") == id);
        Assert.Equal(ActivityKind.Consumer, process.Kind);
        Assert.Equal(send.TraceId, process.TraceId);
        Assert.Equal(("process", queue, "c-1", "queue"), ((string?)process.GetTagItem("messaging.operation.type"), (string?)process.GetTagItem("messaging.destination.name"),
            (string?)process.GetTagItem("messaging.message.conversation_id"), (string?)process.GetTagItem("foundatio.source.kind")));
        var failed = Assert.Single(activities, a => a.OperationName == $"process {queue}" && a.Status == ActivityStatusCode.Error);
        Assert.Equal(typeof(InvalidOperationException).FullName, failed.GetTagItem("error.type"));
        var subscribed = Assert.Single(activities, a => a.OperationName == $"process {topic}");
        Assert.Equal(("tests", "tests"), ((string?)subscribed.GetTagItem("messaging.destination.subscription.name"), (string?)subscribed.GetTagItem("messaging.consumer.group.name")));
        Assert.Equal("publish", Assert.Single(activities, a => a.OperationName == $"publish {topic}").GetTagItem("messaging.operation.name"));

        var sent = measurements.Where(m => m.Instrument == "messaging.client.sent.messages" && Equals(m.Tags.GetValueOrDefault("messaging.destination.name"), queue)).ToList();
        Assert.Equal(2, sent.Sum(m => m.Value));
        Assert.All(sent, m => Assert.Equal(("{message}", "inmemory", "send"), (m.Unit, m.Tags["messaging.system"], m.Tags["messaging.operation.name"])));
        var durations = measurements.Where(m => m.Instrument == "messaging.process.duration" && Equals(m.Tags.GetValueOrDefault("messaging.destination.name"), queue)).ToList();
        Assert.Equal(2, durations.Count);
        Assert.All(durations, m => Assert.Equal(("s", "process", queue), (m.Unit, m.Tags["messaging.operation.name"], m.Tags["foundatio.message.type"])));
        Assert.All(durations, m => Assert.InRange(m.Value, 0, 5));
        var handledCounts = measurements.Where(m => m.Instrument == "foundatio.messaging.handled" && Equals(m.Tags.GetValueOrDefault("messaging.destination.name"), queue)).ToList();
        Assert.Equal(["dead-lettered", "success"], handledCounts.Select(m => (string?)m.Tags["foundatio.outcome"]).Order());
        Assert.Equal(typeof(InvalidOperationException).FullName, Assert.Single(handledCounts, m => m.Tags.ContainsKey("error.type")).Tags["error.type"]);
        Assert.Equal("tests", Assert.Single(measurements, m => m.Instrument == "foundatio.messaging.handled" && Equals(m.Tags.GetValueOrDefault("messaging.destination.name"), topic))
            .Tags["messaging.destination.subscription.name"]);
    }

    private static Dictionary<string, object?> ToDictionary(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var result = new Dictionary<string, object?>(tags.Length);
        foreach (var tag in tags)
            result[tag.Key] = tag.Value;
        return result;
    }

    [Fact]
    public async Task SubscribeAsync_UnderAnActivity_DoesNotRunHandlersInTheSubscribersContext()
    {
        using var source = new ActivitySource("Foundatio.Tests.SubscriberContext");
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == source.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);
        await using var host = await StartAsync(_ => { });

        var handled = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        IAsyncDisposable subscription;
        using (var request = source.StartActivity("request"))
        {
            Assert.NotNull(request);
            subscription = await host.Client.SubscribeAsync<OrderShipped>((_, _, _) =>
            {
                handled.TrySetResult(Activity.Current?.OperationName);
                return Task.CompletedTask;
            }, cancellationToken: TestCancellationToken);
        }

        await using (subscription)
        {
            await host.Client.PublishAsync(new OrderShipped(1), cancellationToken: TestCancellationToken);
            Assert.Null(await handled.Task.WaitAsync(TimeSpan.FromSeconds(10), TestCancellationToken));
        }
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
    public async Task Publish_FromAServiceWhoseTypesTheSubscriberDoesNotKnow_DeliversDerivedEventsToBaseTypeHandlers()
    {
        var received = new ConcurrentQueue<string>();
        await using var subscriber = await StartAsync(m => m.AddSubscriber<AttributedOrderEvent>((message, context, _) =>
        {
            received.Enqueue($"{context.MessageType}:{message.OrderId}:{message.GetType().Name}");
            return Task.CompletedTask;
        }));
        await using var publisher = await MessagingTestHost.StartAsync(_transport, Log, "orders", m => m.MapMessage<AttributedOrderShipped>("partner.order-shipped", topic: "tests.order-events"));

        await publisher.Client.PublishAsync(new AttributedOrderShipped(7), cancellationToken: TestCancellationToken);
        await publisher.Client.PublishAsync(new AttributedOrderEvent(8), cancellationToken: TestCancellationToken);

        await WaitForAsync(() => received.Count == 2);
        Assert.Equal(["partner.order-shipped:7:AttributedOrderEvent", "tests.order-event:8:AttributedOrderEvent"], received.Order(StringComparer.Ordinal));
        Assert.True(await _transport.WaitUntilIdleAsync(TimeSpan.FromSeconds(5), TestCancellationToken));
        Assert.Equal(2, received.Count);
    }

    [Fact]
    public async Task Publish_FromAnotherServiceToAbstractAndInterfaceHandlers_DeliversDerivedEventsAsTheirOwnTypes()
    {
        var received = new ConcurrentQueue<string>();
        await using var subscriber = await StartAsync(m => m
            .MapMessage<IInventoryEvent>("tests.inventory-event", topic: "tests.inventory-events")
            .AddSubscriber<ShipmentEvent>((message, _, _) =>
            {
                received.Enqueue(message is ShipmentDelivered delivered ? $"delivered {delivered.ShipmentId} {delivered.Signature}" : message.GetType().Name);
                return Task.CompletedTask;
            })
            .AddSubscriber<IInventoryEvent>((message, _, _) =>
            {
                received.Enqueue($"{message.GetType().Name} {message.Sku}");
                return Task.CompletedTask;
            })
            .AddSubscriber<AttributedOrderEvent>((message, _, _) =>
            {
                received.Enqueue($"{message.GetType().Name} {message.OrderId}");
                return Task.CompletedTask;
            }));
        await using var publisher = await MessagingTestHost.StartAsync(_transport, Log, "shipping", _ => { });

        await publisher.Client.PublishAsync(new ShipmentDelivered(1, "J. Doe"), cancellationToken: TestCancellationToken);
        await publisher.Client.PublishAsync(new StockDepleted(42), cancellationToken: TestCancellationToken);
        await publisher.Client.PublishAsync(new AttributedOrderShipped(7), cancellationToken: TestCancellationToken);

        Assert.True(await _transport.WaitUntilIdleAsync(TimeSpan.FromSeconds(10), TestCancellationToken));
        Assert.Equal(["AttributedOrderShipped 7", "StockDepleted 42", "delivered 1 J. Doe"], received.Order(StringComparer.Ordinal));
        Assert.Equal(0, _transport.GetMessageCount(MessageSource.DeadLetter(MessageSource.Subscription("tests.shipment-events", "tests"))));
        Assert.Equal(0, _transport.GetMessageCount(MessageSource.DeadLetter(MessageSource.Subscription("tests.inventory-events", "tests"))));
    }

    [Fact]
    public async Task Publish_OfAnUnknownTypeWhoseKnownBaseTypesAreAbstract_IsSkippedWithOneWarningInsteadOfDeadLettered()
    {
        var received = new ConcurrentQueue<ShipmentEvent>();
        await using var subscriber = await StartAsync(m => m.AddSubscriber<ShipmentEvent>((message, _, _) =>
        {
            received.Enqueue(message);
            return Task.CompletedTask;
        }));

        for (int i = 0; i < 2; i++)
            await _transport.SendAsync(MessageDestination.Topic("tests.shipment-events"), [UnknownShipmentEvent()], TestCancellationToken);

        Assert.True(await _transport.WaitUntilIdleAsync(TimeSpan.FromSeconds(10), TestCancellationToken));
        Assert.Empty(received);
        Assert.Equal(0, _transport.GetMessageCount(MessageSource.DeadLetter(MessageSource.Subscription("tests.shipment-events", "tests"))));
        var warning = Assert.Single(Log.LogEntries, e => e.LogLevel == Microsoft.Extensions.Logging.LogLevel.Warning && e.Message.Contains("carrier.shipment-lost", StringComparison.Ordinal));
        Assert.Contains("abstract", warning.Message);
        Assert.Contains("MapMessage", warning.Message);
        Assert.DoesNotContain(Log.LogEntries, e => e.Message.Contains("Could not deserialize", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Send_OfAnUnknownTypeToAnAbstractBaseConsumer_IsRetriedThenDeadLetteredWithoutDeserializing()
    {
        await using var consumer = await StartAsync(m => m
            .Configure(o => o.DefaultRetryPolicy = new RetryPolicy { MaxAttempts = 2, Delay = RetryPolicy.Fixed(TimeSpan.FromMilliseconds(50)) })
            .AddConsumer<ShipmentEvent>((_, _, _) => Task.CompletedTask));

        await _transport.SendAsync(MessageDestination.Queue("tests.shipment-event"), [UnknownShipmentEvent()], TestCancellationToken);

        await WaitForAsync(() => _transport.GetMessageCount(MessageSource.DeadLetter(MessageSource.Queue("tests.shipment-event"))) == 1);
        var deadLetter = Assert.Single(await ReceiveDeadLettersAsync(MessageSource.Queue("tests.shipment-event")));
        Assert.Contains("carrier.shipment-lost", deadLetter.Headers[KnownHeaders.DeadLetterReason]);
        Assert.DoesNotContain("Deserialization failed", deadLetter.Headers[KnownHeaders.DeadLetterReason]);
    }

    [Fact]
    public async Task AddSubscriber_ForABaseTypeWithADerivedTypeWhoseNameIsMappedToAnotherType_StartsAndKeepsTheMapping()
    {
        var notes = new ConcurrentQueue<string>();
        await using var host = await StartAsync(m => m
            .MapMessage<Note>("tests.claim-filed", topic: "tests.notes")
            .AddSubscriber<ClaimEvent>((_, _, _) => Task.CompletedTask)
            .AddSubscriber<Note>((message, _, _) =>
            {
                notes.Enqueue(message.Text);
                return Task.CompletedTask;
            }));

        await host.Client.PublishAsync(new Note("hello"), cancellationToken: TestCancellationToken);

        await WaitForAsync(() => notes.Count == 1);
        Assert.Equal(["hello"], notes);
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
        var witnessed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var witness = await host.Client.SubscribeAsync<OrderShipped>((_, _, _) => { witnessed.TrySetResult(); return Task.CompletedTask; }, cancellationToken: TestCancellationToken);
        await host.Client.PublishAsync(new OrderShipped(2), cancellationToken: TestCancellationToken);
        await witnessed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestCancellationToken);

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
    public async Task RetryDelays_ShorterThanTheTransportsMinimum_AreHeldInProcess()
    {
        var coarse = new LimitedTransport(_transport, _transport.Capabilities with { MinRetryDelay = TimeSpan.FromSeconds(1) });
        var attempts = new ConcurrentQueue<TimeSpan>();
        var stopwatch = Stopwatch.StartNew();
        await using var host = await MessagingTestHost.StartAsync(coarse, Log, "tests", m => m.AddConsumer<PlaceOrder>((_, context, _) =>
        {
            attempts.Enqueue(stopwatch.Elapsed);
            if (context.Attempt == 1)
                context.Retry(TimeSpan.FromMilliseconds(300));
            else if (context.Attempt == 2)
                context.Retry(TimeSpan.FromSeconds(1));

            return Task.CompletedTask;
        }));

        await host.Client.SendAsync(new PlaceOrder(1), cancellationToken: TestCancellationToken);
        await WaitForAsync(() => attempts.Count == 3);

        var times = attempts.ToArray();
        Assert.True(times[1] - times[0] >= TimeSpan.FromMilliseconds(250), $"The short retry was held for its backoff; retried after {times[1] - times[0]}.");
        Assert.Equal([TimeSpan.Zero, TimeSpan.FromSeconds(1)], coarse.AbandonDelays.ToArray());
    }

    [Theory]
    [InlineData(800, 1000)]
    [InlineData(999, 1000)]
    [InlineData(790, 0)]
    public async Task RetryDelays_JustUnderTheTransportsMinimum_AreLengthenedToItInsteadOfHeld(int retryDelayMs, int abandonDelayMs)
    {
        var coarse = new LimitedTransport(_transport, _transport.Capabilities with { MinRetryDelay = TimeSpan.FromSeconds(1) });
        int attempts = 0;
        await using var host = await MessagingTestHost.StartAsync(coarse, Log, "tests", m => m.AddConsumer<PlaceOrder>((_, context, _) =>
        {
            Interlocked.Increment(ref attempts);
            if (context.Attempt == 1)
                context.Retry(TimeSpan.FromMilliseconds(retryDelayMs));

            return Task.CompletedTask;
        }));

        await host.Client.SendAsync(new PlaceOrder(1), cancellationToken: TestCancellationToken);
        await WaitForAsync(() => Volatile.Read(ref attempts) == 2);

        Assert.Equal([TimeSpan.FromMilliseconds(abandonDelayMs)], coarse.AbandonDelays.ToArray());
    }

    [Fact]
    public async Task RetryDelays_FromTheDefaultPolicy_AreNotHeldOnATransportWithAOneSecondMinimum()
    {
        const int count = 16;
        var coarse = new LimitedTransport(_transport, _transport.Capabilities with { MinRetryDelay = TimeSpan.FromSeconds(1) });
        int succeeded = 0;
        await using var host = await MessagingTestHost.StartAsync(coarse, Log, "tests", m => m.AddConsumer<PlaceOrder>((_, context, _) =>
        {
            if (context.Attempt == 1)
                throw new InvalidOperationException("transient");

            Interlocked.Increment(ref succeeded);
            return Task.CompletedTask;
        }, new HandlerOptions { MaxConcurrency = count }));

        await host.Client.SendBatchAsync(Enumerable.Range(1, count).Select(i => new PlaceOrder(i)), cancellationToken: TestCancellationToken);
        await WaitForAsync(() => Volatile.Read(ref succeeded) == count);

        Assert.Equal(count, coarse.AbandonDelays.Count);
        Assert.All(coarse.AbandonDelays, delay => Assert.InRange(delay, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1.2)));
    }

    [Fact]
    public async Task LostDelivery_OnATransportWithoutLeaseExpiration_CancelsTheHandlerAndSkipsSettlement()
    {
        var offsets = new LimitedTransport(_transport, _transport.Capabilities with { LeaseExpiration = false });
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool revoked = false;
        offsets.OnRenew = _ =>
        {
            if (Volatile.Read(ref revoked))
                throw new MessageLeaseLostException("The partition was revoked");
        };

        await using var host = await MessagingTestHost.StartAsync(offsets, Log, "tests", m => m.AddConsumer<PlaceOrder>(async (_, _, ct) =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                throw;
            }
        }, new HandlerOptions { LeaseDuration = TimeSpan.FromMinutes(5) }));

        await host.Client.SendAsync(new PlaceOrder(1), cancellationToken: TestCancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestCancellationToken);
        Volatile.Write(ref revoked, true);

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestCancellationToken);
        await Task.Delay(100, TestCancellationToken);
        Assert.Empty(offsets.AbandonDelays);
        Assert.Equal(1, _transport.GetMessageCount(MessageSource.Queue(QueueName<PlaceOrder>())));
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

    [Fact]
    public async Task Admin_PagesDeadLettersAndReplaysOrDeletesOneById()
    {
        bool fixedHandler = false;
        var handled = new ConcurrentQueue<int>();
        await using var host = await StartAsync(m => m.AddConsumer<PlaceOrder>((message, _, _) =>
        {
            if (!Volatile.Read(ref fixedHandler))
                throw new InvalidOperationException($"order {message.OrderId} is invalid");

            handled.Enqueue(message.OrderId);
            return Task.CompletedTask;
        }, new HandlerOptions { RetryPolicy = RetryPolicy.None }));
        var admin = host.Services.GetRequiredService<IMessageAdmin>();
        var source = MessageSource.Queue(QueueName<PlaceOrder>());

        await host.Client.SendBatchAsync(Enumerable.Range(0, 25).Select(i => new PlaceOrder(i)), o => new MessageKeys { MessageId = $"order-{o.OrderId}" }, cancellationToken: TestCancellationToken);
        await WaitForAsync(() => _transport.GetMessageCount(MessageSource.DeadLetter(source)) == 25);

        var first = await admin.PeekDeadLettersAsync(source, cancellationToken: TestCancellationToken);
        var second = await admin.PeekDeadLettersAsync(source, skip: 20, max: 20, cancellationToken: TestCancellationToken);
        Assert.Equal(20, first.Count);
        Assert.Equal(5, second.Count);
        Assert.Equal(25, first.Concat(second).Select(d => d.MessageId).Distinct().Count());
        Assert.Equal(first.Select(d => d.MessageId), (await admin.PeekDeadLettersAsync(source, skip: 0, max: 20, cancellationToken: TestCancellationToken)).Select(d => d.MessageId));
        Assert.Empty(await admin.PeekDeadLettersAsync(source, skip: 25, max: 20, cancellationToken: TestCancellationToken));

        Volatile.Write(ref fixedHandler, true);
        Assert.True(await admin.ReplayDeadLetterAsync(source, "order-21", TestCancellationToken));
        await WaitForAsync(() => handled.Contains(21));
        Assert.True(await admin.DeleteDeadLetterAsync(source, "order-7", TestCancellationToken));
        Assert.False(await admin.DeleteDeadLetterAsync(source, "order-7", TestCancellationToken));
        Assert.False(await admin.ReplayDeadLetterAsync(source, "missing", TestCancellationToken));

        var remaining = await admin.PeekDeadLettersAsync(source, max: 100, cancellationToken: TestCancellationToken);
        Assert.Equal(23, remaining.Count);
        Assert.DoesNotContain(remaining, d => d.MessageId is "order-21" or "order-7");
        Assert.Equal([21], handled);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => admin.PeekDeadLettersAsync(source, skip: -1, max: 20, cancellationToken: TestCancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => admin.ReplayDeadLetterAsync(source, "", TestCancellationToken));
    }

    [Fact]
    public async Task Admin_GivesTheSourcesOfThisAppsHandlersAndOfAMessageType()
    {
        await using var host = await StartAsync(m => m
            .UseServiceName("billing")
            .MapMessage<OrderShipped>("orders.shipped")
            .AddConsumer<PlaceOrder>((_, _, _) => Task.CompletedTask)
            .AddSubscriber<OrderShipped>((_, _, _) => Task.CompletedTask)
            .AddSubscriber<OrderShipped>((_, _, _) => Task.CompletedTask, new SubscriptionOptions { Name = "audit" })
            .AddSubscriber<UserRegistered>((_, _, _) => Task.CompletedTask, new SubscriptionOptions { Scope = SubscriptionScope.Instance }));
        var admin = host.Services.GetRequiredService<IMessageAdmin>();

        Assert.Equal(MessageSource.Queue(QueueName<PlaceOrder>()), admin.GetQueue<PlaceOrder>());
        Assert.Equal(MessageSource.Subscription("orders.shipped", "billing"), admin.GetSubscription<OrderShipped>());
        Assert.Equal(MessageSource.Subscription("orders.shipped", "audit"), admin.GetSubscription<OrderShipped>("audit"));
        Assert.Equal(
            [MessageSource.Queue(QueueName<PlaceOrder>()), MessageSource.Subscription("orders.shipped", "audit"), MessageSource.Subscription("orders.shipped", "billing")],
            admin.GetSources().OrderBy(s => s.ToString(), StringComparer.Ordinal));
    }

    [Fact]
    public async Task AdminGetStatsAsync_CountsTheSourceThroughTheTransportAndRejectsDeadLetterSources()
    {
        await using var host = await StartAsync(m => m.AddConsumer<PlaceOrder>((_, context, _) =>
        {
            context.DeadLetter("not today");
            return Task.CompletedTask;
        }));
        var admin = host.Services.GetRequiredService<IMessageAdmin>();
        var source = MessageSource.Queue(QueueName<PlaceOrder>());

        await host.Client.SendAsync(new PlaceOrder(1), cancellationToken: TestCancellationToken);
        await WaitForAsync(() => _transport.GetMessageCount(MessageSource.DeadLetter(source)) == 1);

        var stats = await admin.GetStatsAsync(source, TestCancellationToken);
        Assert.NotNull(stats);
        Assert.Equal((source, 0L, 0L, 0L, 1L), (stats.Source, stats.Queued, stats.Delayed, stats.InFlight, stats.DeadLettered));
        Assert.Null(await admin.GetStatsAsync(MessageSource.InstanceSubscription("unused.topic"), TestCancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => admin.GetStatsAsync(MessageSource.DeadLetter(source), TestCancellationToken));
    }

    [Fact]
    public async Task ReplayDeadLetters_WhenTheHandlerStillFails_ReplaysEachDeadLetterOncePerCall()
    {
        int calls = 0;
        await using var host = await StartAsync(m => m.AddConsumer<PlaceOrder>((_, _, _) =>
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("still broken");
        }, new HandlerOptions { RetryPolicy = RetryPolicy.None }));
        var admin = host.Services.GetRequiredService<IMessageAdmin>();
        var source = MessageSource.Queue(QueueName<PlaceOrder>());

        await host.Client.SendAsync(new PlaceOrder(1), cancellationToken: TestCancellationToken);
        await host.Client.SendAsync(new PlaceOrder(2), cancellationToken: TestCancellationToken);
        await WaitForAsync(() => _transport.GetMessageCount(MessageSource.DeadLetter(source)) == 2);

        int replayed = await admin.ReplayDeadLettersAsync(source, max: 20, cancellationToken: TestCancellationToken);
        await WaitForAsync(() => Volatile.Read(ref calls) >= 4 && _transport.GetMessageCount(MessageSource.DeadLetter(source)) == 2);

        Assert.Equal(2, replayed);
        Assert.Equal(4, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task PeekAndReplayDeadLetters_EveryReceive_WaitsLongEnoughToLongPoll()
    {
        var waits = new ConcurrentQueue<TimeSpan>();
        var transport = new InterceptingMessageTransport(_transport, _transport.Capabilities with { MaxReceiveBatchSize = 1 })
        {
            OnReceive = (source, _, wait) =>
            {
                if (source.Kind == MessageSourceKind.DeadLetter)
                    waits.Enqueue(wait);
            }
        };
        await using var host = await MessagingTestHost.StartAsync(transport, Log, "tests", m => m.AddConsumer<PlaceOrder>(
            (_, _, _) => throw new InvalidOperationException("broken"), new HandlerOptions { RetryPolicy = RetryPolicy.None }));
        var admin = host.Services.GetRequiredService<IMessageAdmin>();
        var source = MessageSource.Queue(QueueName<PlaceOrder>());
        await host.Client.SendBatchAsync([new PlaceOrder(1), new PlaceOrder(2)], cancellationToken: TestCancellationToken);
        await WaitForAsync(() => _transport.GetMessageCount(MessageSource.DeadLetter(source)) == 2);

        Assert.Equal(2, (await admin.PeekDeadLettersAsync(source, cancellationToken: TestCancellationToken)).Count);
        Assert.Equal(2, await admin.ReplayDeadLettersAsync(source, cancellationToken: TestCancellationToken));

        Assert.True(waits.Count >= 4);
        Assert.All(waits, wait => Assert.True(wait >= TimeSpan.FromSeconds(1), $"A dead-letter receive waited only {wait}."));
    }

    [Fact]
    public async Task AddSubscriber_HandlersSharingASubscription_AllRunAgainWhenOneThrows()
    {
        var calls = new HandlerCalls { FailCrmSyncOnFirstAttempt = true };
        var policy = new RetryPolicy { MaxAttempts = 3, Delay = RetryPolicy.Fixed(TimeSpan.FromMilliseconds(10)) };
        await using var host = await StartAsync(m => m
            .AddSubscriber<WelcomeEmailHandler>(new SubscriptionOptions { RetryPolicy = policy })
            .AddSubscriber<CrmSyncHandler>(new SubscriptionOptions { RetryPolicy = policy }), s => s.AddSingleton(calls));

        await host.Client.PublishAsync(new UserRegistered(1), cancellationToken: TestCancellationToken);

        await WaitForAsync(() => calls.Count(nameof(CrmSyncHandler)) == 2);
        Assert.Equal(2, calls.Count(nameof(WelcomeEmailHandler)));
    }

    [Fact]
    public async Task AddSubscriber_WithItsOwnSubscriptionName_IsRetriedOnItsOwn()
    {
        var calls = new HandlerCalls { FailCrmSyncOnFirstAttempt = true };
        await using var host = await StartAsync(m => m
            .Configure(o => o.DefaultRetryPolicy = new RetryPolicy { MaxAttempts = 3, Delay = RetryPolicy.Fixed(TimeSpan.FromMilliseconds(10)) })
            .AddSubscriber<WelcomeEmailHandler>()
            .AddSubscriber<CrmSyncHandler>(new SubscriptionOptions { Name = "crm-sync" }), s => s.AddSingleton(calls));

        await host.Client.PublishAsync(new UserRegistered(1), cancellationToken: TestCancellationToken);

        await WaitForAsync(() => calls.Count(nameof(CrmSyncHandler)) == 2);
        Assert.True(await _transport.WaitUntilIdleAsync(TimeSpan.FromSeconds(10), TestCancellationToken));
        Assert.Equal(1, calls.Count(nameof(WelcomeEmailHandler)));
    }

    [Fact]
    public async Task AddSubscriber_HandlersSharingASubscriptionWithDifferentRetryPolicies_FailsAtStartupNamingBoth()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(m => m
            .AddSubscriber<WelcomeEmailHandler>()
            .AddSubscriber<CrmSyncHandler>(new SubscriptionOptions { RetryPolicy = RetryPolicy.None }), s => s.AddSingleton(new HandlerCalls())));

        Assert.Contains("WelcomeEmailHandler and CrmSyncHandler", error.Message);
        Assert.Contains($"subscription:{QueueName<UserRegistered>()}/tests", error.Message);
        Assert.Contains("RetryPolicy", error.Message);
        Assert.Contains("SubscriptionOptions.Name", error.Message);
    }

    [Fact]
    public async Task AddSubscriber_OneHandlerSetsALeaseAndAnotherLeavesItUnset_FailsAtStartup()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(m => m
            .AddSubscriber<WelcomeEmailHandler>(new SubscriptionOptions { LeaseDuration = TimeSpan.FromMinutes(5) })
            .AddSubscriber<CrmSyncHandler>(), s => s.AddSingleton(new HandlerCalls())));

        Assert.Contains("LeaseDuration values (00:05:00 and not set)", error.Message);
    }

    [Fact]
    public async Task AddSubscriber_InstanceSubscribersWithDifferentConcurrency_FailsAtStartup()
    {
        var instance = new SubscriptionOptions { Scope = SubscriptionScope.Instance };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(m => m
            .AddSubscriber<WelcomeEmailHandler>(instance)
            .AddSubscriber<CrmSyncHandler>(instance with { MaxConcurrency = 1 }), s => s.AddSingleton(new HandlerCalls())));

        Assert.Contains($"instance:{QueueName<UserRegistered>()}/tests", error.Message);
        Assert.Contains("MaxConcurrency", error.Message);
    }

    [Fact]
    public async Task AddSubscriber_InstanceSubscribersWithTheirOwnNames_GetSeparateSubscriptions()
    {
        var calls = new HandlerCalls();
        await using var host = await StartAsync(m => m
            .AddSubscriber<WelcomeEmailHandler>(new SubscriptionOptions { Scope = SubscriptionScope.Instance })
            .AddSubscriber<CrmSyncHandler>(new SubscriptionOptions { Scope = SubscriptionScope.Instance, Name = "crm", MaxConcurrency = 1 }), s => s.AddSingleton(calls));

        await host.Client.PublishAsync(new UserRegistered(1), cancellationToken: TestCancellationToken);

        await WaitForAsync(() => calls.Count(nameof(CrmSyncHandler)) == 1 && calls.Count(nameof(WelcomeEmailHandler)) == 1);
        Assert.Equal(2, host.Services.GetRequiredService<MessagingStatus>().GetProcessors().Count);
    }

    [Fact]
    public async Task AddConsumer_TypesRoutedToOneQueueWithDifferentOptions_FailsAtStartup()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(m => m
            .MapMessage<PlaceOrder>("tests.place-order", queue: "tests.orders")
            .MapMessage<ShipOrder>("tests.ship-order", queue: "tests.orders")
            .AddConsumer<PlaceOrder>((_, _, _) => Task.CompletedTask, new HandlerOptions { MaxConcurrency = 2 })
            .AddConsumer<ShipOrder>((_, _, _) => Task.CompletedTask)));

        Assert.Contains("PlaceOrder delegate and ShipOrder delegate share queue:tests.orders", error.Message);
        Assert.Contains("MaxConcurrency values (2 and not set)", error.Message);
        Assert.Contains("a queue of its own", error.Message);
    }

    [Fact]
    public async Task AddSubscriber_HandlersSharingOneRetryPolicyInstance_Start()
    {
        var policy = RetryPolicy.Default.DoNotRetry<ArgumentException>();
        await using var host = await StartAsync(m => m
            .AddSubscriber<WelcomeEmailHandler>(new SubscriptionOptions { RetryPolicy = policy, MaxConcurrency = 2 })
            .AddSubscriber<CrmSyncHandler>(new SubscriptionOptions { RetryPolicy = policy, MaxConcurrency = 2 }), s => s.AddSingleton(new HandlerCalls()));

        Assert.Single(host.Services.GetRequiredService<MessagingStatus>().GetProcessors());
    }

    [Fact]
    public async Task AddSubscriber_HandlersSharingPoliciesBuiltTheSameWay_Start()
    {
        await using var host = await StartAsync(m => m
            .AddSubscriber<WelcomeEmailHandler>(new SubscriptionOptions { RetryPolicy = new RetryPolicy { MaxAttempts = 3 } })
            .AddSubscriber<CrmSyncHandler>(new SubscriptionOptions { RetryPolicy = new RetryPolicy { MaxAttempts = 3 } })
            .AddSubscriber<OrderShipped>((_, _, _) => Task.CompletedTask, new SubscriptionOptions { RetryPolicy = new RetryPolicy { Delay = RetryPolicy.Fixed(TimeSpan.FromSeconds(1)) }.DoNotRetry<ArgumentException>() })
            .AddSubscriber<OrderCancelled>((_, _, _) => Task.CompletedTask, new SubscriptionOptions { RetryPolicy = new RetryPolicy { Delay = RetryPolicy.Fixed(TimeSpan.FromSeconds(1)) }.DoNotRetry<ArgumentException>() }),
            s => s.AddSingleton(new HandlerCalls()));

        Assert.Equal(3, host.Services.GetRequiredService<MessagingStatus>().GetProcessors().Count);
        Assert.Equal(RetryPolicy.Default, new RetryPolicy());
        Assert.Equal(RetryPolicy.Exponential(TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(1)), RetryPolicy.Exponential(TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task AddSubscriber_HandlersSharingPoliciesWithDifferentDelegates_FailsAtStartupSayingSoOnce()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(m => m
            .AddSubscriber<WelcomeEmailHandler>(new SubscriptionOptions { RetryPolicy = new RetryPolicy { MaxAttempts = 3, Delay = _ => TimeSpan.FromSeconds(1) } })
            .AddSubscriber<CrmSyncHandler>(new SubscriptionOptions { RetryPolicy = new RetryPolicy { MaxAttempts = 3, Delay = _ => TimeSpan.FromSeconds(2) } }),
            s => s.AddSingleton(new HandlerCalls())));

        Assert.Contains("two policies with MaxAttempts 3 but different Delay delegates; share one RetryPolicy instance", error.Message);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(error.Message, "MaxAttempts 3"));
    }

    [Fact]
    public void AddConsumer_WithSubscriptionOptionsThatNameASubscription_Throws()
    {
        var services = new ServiceCollection();

        var named = Assert.Throws<ArgumentException>(() => services.AddFoundatioMessaging(m => m.AddConsumer<PlaceOrderHandler>(new SubscriptionOptions { Name = "orders-own" })));
        Assert.Throws<ArgumentException>(() => services.AddFoundatioMessaging(m => m.AddConsumer<PlaceOrder>((_, _, _) => Task.CompletedTask, new SubscriptionOptions { Scope = SubscriptionScope.Instance })));
        services.AddFoundatioMessaging(m => m.AddConsumer<ShipOrder>((_, _, _) => Task.CompletedTask, new SubscriptionOptions { MaxConcurrency = 2 }));

        Assert.Contains("[Message(Queue = ...)] or MapMessage", named.Message);
    }

    [Fact]
    public async Task ServicesConfigure_OfMessagingOptions_IsAppliedInOrderWithTheBuilder()
    {
        await using var host = await StartAsync(m => m.AddSubscriber<OrderShipped>((_, _, _) => Task.CompletedTask).Services.Configure<MessagingOptions>(o => o.ServiceName = "billing"),
            s => s.Configure<MessagingOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(3)));

        Assert.Equal($"subscription:{QueueName<OrderShipped>()}/billing", Assert.Single(host.Services.GetRequiredService<MessagingStatus>().GetProcessors()).Name);
        Assert.Equal(TimeSpan.FromSeconds(5), host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<MessagingOptions>>().Value.ShutdownTimeout);
    }

    [Fact]
    public async Task Send_OfATypeThisAppOnlySubscribesTo_WarnsOnceSuggestingPublish()
    {
        await using var host = await StartAsync(m => m
            .AddSubscriber<OrderShipped>((_, _, _) => Task.CompletedTask)
            .AddConsumer<PlaceOrder>((_, _, _) => Task.CompletedTask));

        await host.Client.SendAsync(new OrderShipped(1), cancellationToken: TestCancellationToken);
        await host.Client.SendAsync(new OrderShipped(2), cancellationToken: TestCancellationToken);
        await host.Client.SendAsync(new PlaceOrder(1), cancellationToken: TestCancellationToken);
        await host.Client.PublishAsync(new OrderShipped(3), cancellationToken: TestCancellationToken);

        var warning = Assert.Single(Log.LogEntries, e => e.LogLevel == Microsoft.Extensions.Logging.LogLevel.Warning);
        Assert.StartsWith($"{QueueName<OrderShipped>()} was sent with SendAsync", warning.Message);
        Assert.Contains("publish events with PublishAsync", warning.Message);
    }

    [Fact]
    public async Task Publish_OfATypeThisAppOnlyConsumes_WarnsOnceSuggestingSend()
    {
        await using var host = await StartAsync(m => m
            .AddConsumer<PlaceOrder>((_, _, _) => Task.CompletedTask)
            .AddSubscriber<OrderShipped>((_, _, _) => Task.CompletedTask));

        await host.Client.PublishAsync(new PlaceOrder(1), cancellationToken: TestCancellationToken);
        await host.Client.PublishAsync(new PlaceOrder(2), cancellationToken: TestCancellationToken);
        await host.Client.PublishAsync(new OrderShipped(1), cancellationToken: TestCancellationToken);
        await host.Client.SendAsync(new PlaceOrder(3), cancellationToken: TestCancellationToken);

        var warning = Assert.Single(Log.LogEntries, e => e.LogLevel == Microsoft.Extensions.Logging.LogLevel.Warning);
        Assert.StartsWith($"{QueueName<PlaceOrder>()} was published with PublishAsync", warning.Message);
        Assert.Contains("send commands with SendAsync", warning.Message);
    }

    [Fact]
    public void AddConsumer_OfAHandlerWithAMissingDependency_FailsWhenTheHostIsBuiltWithValidateOnBuild()
    {
        var services = new ServiceCollection();
        services.AddFoundatioMessaging(m => m.UseInMemory().AddConsumer<NeedsMissingServiceHandler>().AddSubscriber<WelcomeEmailHandler>());
        services.AddSingleton(new HandlerCalls());

        var error = Assert.Throws<AggregateException>(() => services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }));

        Assert.Contains(nameof(NeedsMissingServiceHandler), error.Message);
        Assert.Contains(nameof(IMissingService), error.Message);
        Assert.DoesNotContain(nameof(WelcomeEmailHandler), error.Message);
    }

    [Fact]
    public async Task MessagingOptionsTimeProvider_RunsTheRuntimeAndTheInMemoryTransportOnItInsteadOfTheRegisteredClock()
    {
        var registered = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow.AddDays(-30));
        var handled = new TaskCompletionSource<IMessageContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(Log);
        services.AddSingleton<TimeProvider>(registered);
        services.AddFoundatioMessaging(m => m
            .UseInMemory()
            .Configure(o => o.TimeProvider = TimeProvider.System)
            .AddConsumer<PlaceOrder>((_, context, _) => { handled.TrySetResult(context); return Task.CompletedTask; }));
        await using var provider = services.BuildServiceProvider();
        var hosted = Assert.Single(provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>());
        await hosted.StartAsync(TestCancellationToken);

        await provider.GetRequiredService<IMessageClient>().SendAsync(new PlaceOrder(1), new SendOptions { Delay = TimeSpan.FromMilliseconds(50) }, TestCancellationToken);

        var context = await handled.Task.WaitAsync(TimeSpan.FromSeconds(10), TestCancellationToken);
        Assert.True(context.SentAt > DateTimeOffset.UtcNow.AddMinutes(-1));
        await hosted.StopAsync(TestCancellationToken);
    }

    [Fact]
    public async Task AddSubscriber_WithOptionsFromServices_UsesTheSubscriptionNameTheyGive()
    {
        await using var host = await StartAsync(m => m.AddSubscriber<WelcomeEmailHandler>(sp => new SubscriptionOptions { Name = sp.GetRequiredService<SubscriptionSettings>().Name }),
            s => s.AddSingleton(new HandlerCalls()).AddSingleton(new SubscriptionSettings("welcome")));

        Assert.Equal($"subscription:{QueueName<UserRegistered>()}/welcome", Assert.Single(host.Services.GetRequiredService<MessagingStatus>().GetProcessors()).Name);
    }

    [Fact]
    public async Task AddConsumer_WithOptionsFromServicesThatNameASubscription_FailsAtStartup()
    {
        var error = await Assert.ThrowsAsync<ArgumentException>(() => StartAsync(m => m.AddConsumer<PlaceOrderHandler>(_ => new SubscriptionOptions { Name = "orders-own" }),
            s => s.AddSingleton(new Recorder()).AddScoped<ScopedDependency>()));

        Assert.Contains("SubscriptionOptions.Name and Scope do not apply", error.Message);
    }

    [Fact]
    public void AddSubscriber_WithoutAServiceNameOnABrokerTransport_FailsAtStartupListingTheSubscriptions()
    {
        using var provider = BuildServices(m => m
            .UseTransport(new InterceptingMessageTransport(_transport))
            .AddSubscriber<WelcomeEmailHandler>()
            .AddSubscriber<OrderShipped>((_, _, _) => Task.CompletedTask)
            .AddSubscriber<OrderCancelled>((_, _, _) => Task.CompletedTask, new SubscriptionOptions { Name = "cancellations" })
            .AddSubscriber<Note>((_, _, _) => Task.CompletedTask, new SubscriptionOptions { Scope = SubscriptionScope.Instance })
            .AddConsumer<PlaceOrder>((_, _, _) => Task.CompletedTask));

        var error = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IMessageClient>());

        Assert.Contains("UseServiceName(\"orders\")", error.Message);
        Assert.Contains($"WelcomeEmailHandler (topic {QueueName<UserRegistered>()})", error.Message);
        Assert.Contains($"OrderShipped delegate (topic {QueueName<OrderShipped>()})", error.Message);
        Assert.DoesNotContain("OrderCancelled", error.Message);
        Assert.DoesNotContain("Note delegate", error.Message);
        Assert.DoesNotContain("PlaceOrder", error.Message);
    }

    [Fact]
    public void AddSubscriber_WhenEveryServiceSubscriptionIsNamed_NeedsNoServiceNameOnABrokerTransport()
    {
        using var provider = BuildServices(m => m
            .UseTransport(new InterceptingMessageTransport(_transport))
            .AddSubscriber<OrderShipped>((_, _, _) => Task.CompletedTask, new SubscriptionOptions { Name = "shipping" })
            .AddSubscriber<Note>((_, _, _) => Task.CompletedTask, new SubscriptionOptions { Scope = SubscriptionScope.Instance })
            .AddConsumer<PlaceOrder>((_, _, _) => Task.CompletedTask));

        Assert.NotNull(provider.GetRequiredService<IMessageClient>());
    }

    [Fact]
    public async Task AddSubscriber_WithoutAServiceNameOnTheInMemoryTransport_UsesTheApplicationName()
    {
        var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(Log);
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new TestHostEnvironment { ApplicationName = "Api" });
        services.AddFoundatioMessaging(m => m.UseTransport(_transport).AddSubscriber<OrderShipped>((_, _, _) => { handled.TrySetResult(); return Task.CompletedTask; }));
        await using var provider = services.BuildServiceProvider();
        var hosted = Assert.Single(provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>());
        await hosted.StartAsync(TestCancellationToken);

        await provider.GetRequiredService<IMessageClient>().PublishAsync(new OrderShipped(1), cancellationToken: TestCancellationToken);

        await handled.Task.WaitAsync(TimeSpan.FromSeconds(10), TestCancellationToken);
        Assert.Equal($"subscription:{QueueName<OrderShipped>()}/Api", Assert.Single(provider.GetRequiredService<MessagingStatus>().GetProcessors()).Name);
        await hosted.StopAsync(TestCancellationToken);
    }

    [Fact]
    public async Task SubscribeAsync_ServiceScopedWithoutAServiceNameOnABrokerTransport_Throws()
    {
        using var provider = BuildServices(m => m.UseTransport(new InterceptingMessageTransport(_transport)));
        var client = provider.GetRequiredService<IMessageClient>();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.SubscribeAsync<OrderShipped>((_, _, _) => Task.CompletedTask,
            new SubscriptionOptions { Scope = SubscriptionScope.Service }, TestCancellationToken));

        Assert.Contains("UseServiceName", error.Message);
        await using var instance = await client.SubscribeAsync<OrderShipped>((_, _, _) => Task.CompletedTask, cancellationToken: TestCancellationToken);
    }

    [Fact]
    public async Task Send_WithOptionsTheTransportIgnores_WarnsOncePerMessageType()
    {
        await using var transport = new InMemoryMessageTransport(new TransportCapabilities());
        await using var host = await MessagingTestHost.StartAsync(transport, Log, "tests", _ => { });

        await host.Client.SendAsync(new PlaceOrder(1), new SendOptions { GroupKey = "customer-1" }, TestCancellationToken);
        await host.Client.SendAsync(new PlaceOrder(2), new SendOptions { GroupKey = "customer-2", DeduplicationId = "order-2" }, TestCancellationToken);
        await host.Client.SendBatchAsync([new PlaceOrder(3), new PlaceOrder(4)], new SendOptions { GroupKey = "customer-3" }, TestCancellationToken);
        await host.Client.PublishAsync(new OrderShipped(1), new PublishOptions { GroupKey = "customer-1" }, TestCancellationToken);

        var warnings = Log.LogEntries.Where(e => e.LogLevel == Microsoft.Extensions.Logging.LogLevel.Warning).Select(e => e.Message).ToList();
        Assert.Single(warnings, w => w.StartsWith($"{QueueName<PlaceOrder>()} was sent with a GroupKey") && w.Contains("may be handled concurrently and out of order"));
        Assert.Single(warnings, w => w.StartsWith($"{QueueName<PlaceOrder>()} was sent with a DeduplicationId") && w.Contains("repeated sends are all delivered"));
        Assert.Single(warnings, w => w.StartsWith($"{QueueName<OrderShipped>()} was sent with a GroupKey"));
        Assert.Equal(4, transport.GetMessageCount(MessageSource.Queue(QueueName<PlaceOrder>())));
    }

    [Fact]
    public async Task Send_WithOptionsTheTransportHonours_DoesNotWarn()
    {
        await using var host = await StartAsync(_ => { });

        await host.Client.SendAsync(new PlaceOrder(1), new SendOptions { GroupKey = "customer-1", DeduplicationId = "order-1" }, TestCancellationToken);

        Assert.DoesNotContain(Log.LogEntries, e => e.Message.Contains("was sent with a"));
    }

    [Fact]
    public async Task Send_WithRequireTransportCapabilities_ThrowsInsteadOfSendingWhatTheTransportIgnores()
    {
        await using var transport = new InMemoryMessageTransport(new TransportCapabilities { OrderedGroups = true });
        await using var host = await MessagingTestHost.StartAsync(transport, Log, "tests", m => m.Configure(o => o.RequireTransportCapabilities = true));

        var deduplication = await Assert.ThrowsAsync<MessageBusException>(() => host.Client.SendAsync(new PlaceOrder(1), new SendOptions { DeduplicationId = "order-1" }, TestCancellationToken));
        Assert.Contains("does not deduplicate", deduplication.Message);
        await host.Client.SendAsync(new PlaceOrder(2), new SendOptions { GroupKey = "customer-2" }, TestCancellationToken);

        await using var unordered = new InMemoryMessageTransport(new TransportCapabilities { Deduplication = true });
        await using var other = await MessagingTestHost.StartAsync(unordered, Log, "tests", m => m.Configure(o => o.RequireTransportCapabilities = true));
        var group = await Assert.ThrowsAsync<MessageBusException>(() => other.Client.PublishAsync(new OrderShipped(1), new PublishOptions { GroupKey = "customer-1" }, TestCancellationToken));
        Assert.Contains("does not keep groups in order", group.Message);

        Assert.Equal(1, transport.GetMessageCount(MessageSource.Queue(QueueName<PlaceOrder>())));
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

    private ServiceProvider BuildServices(Action<FoundatioMessagingBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(Log);
        services.AddSingleton(new HandlerCalls());
        services.AddFoundatioMessaging(configure);
        return services.BuildServiceProvider();
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

    public record UserRegistered(int UserId);

    public record SubscriptionSettings(string Name);

    public interface IMissingService;

    public sealed class NeedsMissingServiceHandler(IMissingService missing) : IMessageHandler<PlaceOrder>
    {
        public Task HandleAsync(PlaceOrder message, IMessageContext context, CancellationToken cancellationToken) => Task.FromResult(missing);
    }

    public sealed class HandlerCalls
    {
        private readonly ConcurrentQueue<string> _calls = new();

        public bool FailCrmSyncOnFirstAttempt { get; init; }

        public void Add(string handler) => _calls.Enqueue(handler);

        public int Count(string handler) => _calls.Count(c => c == handler);
    }

    public sealed class WelcomeEmailHandler(HandlerCalls calls) : IMessageHandler<UserRegistered>
    {
        public Task HandleAsync(UserRegistered message, IMessageContext context, CancellationToken cancellationToken)
        {
            calls.Add(nameof(WelcomeEmailHandler));
            return Task.CompletedTask;
        }
    }

    public sealed class CrmSyncHandler(HandlerCalls calls) : IMessageHandler<UserRegistered>
    {
        public Task HandleAsync(UserRegistered message, IMessageContext context, CancellationToken cancellationToken)
        {
            calls.Add(nameof(CrmSyncHandler));
            if (calls.FailCrmSyncOnFirstAttempt && context.Attempt == 1)
                throw new InvalidOperationException("The CRM is unavailable");

            return Task.CompletedTask;
        }
    }

    private sealed class TestHostEnvironment : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public required string ApplicationName { get; set; }
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    [Message("orders.same")]
    public record SameNameMessage;

    [Message("tests.order-event", Topic = "tests.order-events")]
    public record AttributedOrderEvent(int OrderId);

    [Message("tests.order-shipped", Topic = "tests.order-events")]
    public record AttributedOrderShipped(int OrderId) : AttributedOrderEvent(OrderId);

    [Message("tests.shipment-event", Topic = "tests.shipment-events")]
    public abstract record ShipmentEvent(int ShipmentId);

    [Message("tests.shipment-delivered", Topic = "tests.shipment-events")]
    public record ShipmentDelivered(int ShipmentId, string Signature) : ShipmentEvent(ShipmentId);

    public interface IInventoryEvent
    {
        int Sku { get; }
    }

    [Message("tests.stock-depleted", Topic = "tests.inventory-events")]
    public record StockDepleted(int Sku) : IInventoryEvent;

    [Message("tests.claim-event", Topic = "tests.claim-events")]
    public abstract record ClaimEvent(int ClaimId);

    [Message("tests.claim-filed", Topic = "tests.claim-events")]
    public record ClaimFiled(int ClaimId) : ClaimEvent(ClaimId);

    /// <summary>A shipment event from a carrier's service, of a type this process has never heard of.</summary>
    private static OutgoingMessage UnknownShipmentEvent() => new()
    {
        MessageId = Guid.NewGuid().ToString("N"),
        Body = System.Text.Encoding.UTF8.GetBytes("""{"shipmentId":1,"reason":"lost"}"""),
        Headers = new Dictionary<string, string>
        {
            [KnownHeaders.MessageType] = "carrier.shipment-lost",
            [KnownHeaders.MessageBaseTypes] = "tests.shipment-event"
        }
    };

    public sealed class DisposableDependency : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

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

        /// <summary>The delay of every <see cref="IMessageReceiver.AbandonAsync"/> call.</summary>
        public ConcurrentQueue<TimeSpan> AbandonDelays { get; } = new();

        /// <summary>Runs on every <see cref="IMessageReceiver.RenewAsync"/> call, before the inner receiver renews.</summary>
        public Action<IncomingMessage>? OnRenew { get; set; }

        public async Task<IMessageReceiver> CreateReceiverAsync(MessageSource source, ReceiverOptions options, CancellationToken cancellationToken = default)
            => new Receiver(this, await inner.CreateReceiverAsync(source, options, cancellationToken));

        public Task<MessageSourceStats?> GetStatsAsync(MessageSource source, CancellationToken cancellationToken = default) => inner.GetStatsAsync(source, cancellationToken);

        public Task DeleteAsync(MessageSource source, CancellationToken cancellationToken = default) => inner.DeleteAsync(source, cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class Receiver(LimitedTransport transport, IMessageReceiver inner) : IMessageReceiver
        {
            public MessageSource Source => inner.Source;

            public Task<IReadOnlyList<IncomingMessage>> ReceiveAsync(int maxMessages, TimeSpan maxWait, CancellationToken cancellationToken = default)
                => inner.ReceiveAsync(maxMessages, maxWait, cancellationToken);

            public Task CompleteAsync(IncomingMessage message, CancellationToken cancellationToken = default) => inner.CompleteAsync(message, cancellationToken);

            public Task AbandonAsync(IncomingMessage message, TimeSpan delay, CancellationToken cancellationToken = default)
            {
                transport.AbandonDelays.Enqueue(delay);
                return inner.AbandonAsync(message, delay, cancellationToken);
            }

            public Task DeadLetterAsync(IncomingMessage message, string reason, CancellationToken cancellationToken = default) => inner.DeadLetterAsync(message, reason, cancellationToken);

            public Task RenewAsync(IncomingMessage message, CancellationToken cancellationToken = default)
            {
                transport.OnRenew?.Invoke(message);
                return inner.RenewAsync(message, cancellationToken);
            }

            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}
