using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;
using Foundatio.Monitoring;
using Foundatio.Resilience;
using Foundatio.Testing;
using Foundatio.Utility;
using Foundatio.Xunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Foundatio.Tests.Messaging;

/// <summary>Messaging whose topics are on a transport of their own, set with <see cref="FoundatioMessagingBuilder.ForTopics"/>.</summary>
public class TopicTransportTests : TestWithLoggingBase
{
    private static readonly RetryPolicy OneFastRetry = new() { MaxAttempts = 2, Delay = RetryPolicy.Fixed(TimeSpan.FromMilliseconds(10)) };

    public TopicTransportTests(ITestOutputHelper output) : base(output)
    {
    }

    [Fact]
    public async Task SendAndPublish_WithTopicsOnTheirOwnTransport_UseTheTransportOfTheirKind()
    {
        await using var queues = new RecordedTransport();
        await using var topics = new RecordedTransport();
        var handled = new ConcurrentQueue<string>();
        await using var host = await StartAsync(queues, topics, m => m
            .AddConsumer<PlaceOrder>((_, _, _) => Record(handled, "consumer"))
            .AddSubscriber<OrderPlaced>((_, _, _) => Record(handled, "service"))
            .AddSubscriber<OrderPlaced>((_, _, _) => Record(handled, "instance"), new SubscriptionOptions { Scope = SubscriptionScope.Instance, Name = "split-instance" }));
        await using var subscription = await host.Client.SubscribeAsync<OrderPlaced>((_, _, _) => Record(handled, "runtime"), cancellationToken: TestCancellationToken);

        await host.Client.SendAsync(new PlaceOrder(1), cancellationToken: TestCancellationToken);
        await host.Client.PublishAsync(new OrderPlaced(1), cancellationToken: TestCancellationToken);
        await WaitForAsync(() => handled.Count == 4);

        Assert.Equal(["consumer", "instance", "runtime", "service"], handled.Order(StringComparer.Ordinal));
        Assert.Equal([MessageDestination.Queue("split.place-order")], queues.Sends);
        Assert.Equal([MessageDestination.Topic("split.order-placed")], topics.Sends);
        Assert.Equal([MessageSourceKind.Queue], queues.Receivers.Select(s => s.Kind).Distinct());
        Assert.Equal([MessageSourceKind.Subscription, MessageSourceKind.InstanceSubscription, MessageSourceKind.InstanceSubscription], topics.Receivers.Select(s => s.Kind).Order());
    }

    [Fact]
    public async Task Retry_WithTopicsOnTheirOwnTransport_ReleasesEachDeliveryOnItsTransport()
    {
        await using var queues = new RecordedTransport();
        await using var topics = new RecordedTransport();
        var attempts = new ConcurrentQueue<(string Handler, int Attempt)>();
        await using var host = await StartAsync(queues, topics, m => m
            .AddConsumer<PlaceOrder>((_, context, _) => FailFirst(attempts, "consumer", context), new HandlerOptions { RetryPolicy = OneFastRetry })
            .AddSubscriber<OrderPlaced>((_, context, _) => FailFirst(attempts, "subscriber", context), new SubscriptionOptions { RetryPolicy = OneFastRetry }));

        await host.Client.SendAsync(new PlaceOrder(1), cancellationToken: TestCancellationToken);
        await host.Client.PublishAsync(new OrderPlaced(1), cancellationToken: TestCancellationToken);
        await WaitForAsync(() => attempts.Count == 4);
        await WaitForAsync(() => queues.Inner.GetMessageCount(MessageSource.Queue("split.place-order")) == 0 && topics.Inner.GetMessageCount(MessageSource.Subscription("split.order-placed", "split")) == 0);

        Assert.Equal([("consumer", 1), ("consumer", 2), ("subscriber", 1), ("subscriber", 2)], attempts.Order());
        Assert.Equal(1, queues.Settles.Count(s => s == "abandon"));
        Assert.Equal(1, topics.Settles.Count(s => s == "abandon"));
    }

    [Fact]
    public async Task SendAsync_WithADelayPastTheQueueTransportsLimit_HopsOnTheQueueTransport()
    {
        await using var queues = new RecordedTransport(new TransportCapabilities { MaxDelay = TimeSpan.FromMilliseconds(200), MaxReceiveBatchSize = 100 });
        await using var topics = new RecordedTransport(new TransportCapabilities { MaxDelay = TimeSpan.Zero });
        var handledAt = new TaskCompletionSource<DateTimeOffset>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await StartAsync(queues, topics, m => m.AddConsumer<PlaceOrder>((_, _, _) =>
        {
            handledAt.TrySetResult(DateTimeOffset.UtcNow);
            return Task.CompletedTask;
        }));

        var sentAt = DateTimeOffset.UtcNow;
        await host.Client.SendAsync(new PlaceOrder(1), new SendOptions { Delay = TimeSpan.FromMilliseconds(700) }, TestCancellationToken);

        Assert.True(await handledAt.Task.WaitAsync(TimeSpan.FromSeconds(10), TestCancellationToken) - sentAt >= TimeSpan.FromMilliseconds(650));
        Assert.True(queues.Sends.Count >= 2, $"Expected the delay to hop on the queue transport, saw {queues.Sends.Count} sends.");
        Assert.All(queues.Sends, d => Assert.Equal(MessageDestination.Queue("split.place-order"), d));
        Assert.Empty(topics.Sends);
    }

    [Fact]
    public async Task SendAndPublish_WithTopicsOnTheirOwnTransport_CheckTheCapabilitiesOfTheirTransport()
    {
        await using var queues = new RecordedTransport();
        await using var topics = new RecordedTransport(new TransportCapabilities { MaxDelay = TimeSpan.Zero, MaxMessageSize = 1024 });
        await using var host = await StartAsync(queues, topics, _ => { });
        string large = new('x', 2048);

        await host.Client.SendAsync(new PlaceOrder(1, large), new SendOptions { Delay = TimeSpan.FromMinutes(1) }, TestCancellationToken);
        var delayed = await Assert.ThrowsAsync<MessageBusException>(() => host.Client.PublishAsync(new OrderPlaced(1), new PublishOptions { Delay = TimeSpan.FromMinutes(1) }, TestCancellationToken));
        await Assert.ThrowsAsync<MessageTooLargeException>(() => host.Client.PublishAsync(new OrderPlaced(2, large), cancellationToken: TestCancellationToken));

        Assert.Contains("does not support delayed publishes", delayed.Message);
        Assert.Single(queues.Sends);
        Assert.Empty(topics.Sends);
    }

    [Fact]
    public async Task DeadLetters_WithTopicsOnTheirOwnTransport_AreKeptAndAdministeredOnTheTransportOfTheirSource()
    {
        await using var queues = new RecordedTransport();
        await using var topics = new RecordedTransport();
        bool fail = true;
        var handled = new ConcurrentQueue<string>();
        await using var host = await StartAsync(queues, topics, m => m
            .AddConsumer<PlaceOrder>((_, _, _) => Volatile.Read(ref fail) ? throw new InvalidOperationException("boom") : Record(handled, "consumer"), new HandlerOptions { RetryPolicy = RetryPolicy.None })
            .AddSubscriber<OrderPlaced>((_, _, _) => Volatile.Read(ref fail) ? throw new InvalidOperationException("boom") : Record(handled, "subscriber"), new SubscriptionOptions { RetryPolicy = RetryPolicy.None }));
        var admin = host.Services.GetRequiredService<IMessageAdmin>();
        var queue = admin.GetQueue<PlaceOrder>();
        var subscription = admin.GetSubscription<OrderPlaced>();

        string orderId = await host.Client.SendAsync(new PlaceOrder(1), cancellationToken: TestCancellationToken);
        string eventId = await host.Client.PublishAsync(new OrderPlaced(1), cancellationToken: TestCancellationToken);
        await WaitForAsync(() => queues.Inner.GetMessageCount(MessageSource.DeadLetter(queue)) == 1 && topics.Inner.GetMessageCount(MessageSource.DeadLetter(subscription)) == 1);

        Assert.Equal(0, topics.Inner.GetMessageCount(MessageSource.DeadLetter(queue)));
        Assert.Equal(0, queues.Inner.GetMessageCount(MessageSource.DeadLetter(subscription)));
        Assert.Equal(orderId, Assert.Single(await admin.PeekDeadLettersAsync(queue, cancellationToken: TestCancellationToken)).MessageId);
        Assert.Equal(eventId, Assert.Single(await admin.PeekDeadLettersAsync(subscription, cancellationToken: TestCancellationToken)).MessageId);
        Assert.Equal(1, (await admin.GetStatsAsync(queue, TestCancellationToken))!.DeadLettered);
        Assert.Equal(1, (await admin.GetStatsAsync(subscription, TestCancellationToken))!.DeadLettered);
        Assert.Equal([queue], queues.Stats);
        Assert.Equal([subscription], topics.Stats);

        Volatile.Write(ref fail, false);
        Assert.Equal(1, await admin.ReplayDeadLettersAsync(queue, cancellationToken: TestCancellationToken));
        Assert.True(await admin.ReplayDeadLetterAsync(subscription, eventId, TestCancellationToken));
        await WaitForAsync(() => handled.Count == 2);

        Assert.Equal(["consumer", "subscriber"], handled.Order(StringComparer.Ordinal));
        Assert.Equal([MessageDestination.Queue("split.place-order"), MessageDestination.Queue("split.place-order")], queues.Sends);
        Assert.Equal([MessageDestination.Topic("split.order-placed"), MessageDestination.Subscription("split.order-placed", "split")], topics.Sends);

        await admin.PurgeDeadLettersAsync(queue, TestCancellationToken);
        await admin.PurgeDeadLettersAsync(subscription, TestCancellationToken);
        Assert.False(await admin.DeleteDeadLetterAsync(subscription, "missing", TestCancellationToken));
        Assert.Equal([MessageSource.DeadLetter(queue)], queues.Deletes);
        Assert.Equal([MessageSource.DeadLetter(subscription)], topics.Deletes);
    }

    [Fact]
    public async Task LegacyMessageBus_WithTopicsOnTheirOwnTransport_PublishesAndSubscribesOnTheTopicTransport()
    {
        await using var queues = new RecordedTransport();
        await using var topics = new RecordedTransport();
        await using var host = await StartAsync(queues, topics, m => m.AddLegacyMessageBus());
        var bus = host.Services.GetRequiredService<IMessageBus>();
        var received = new TaskCompletionSource<LockReleased>(TaskCreationOptions.RunContinuationsAsynchronously);

        await bus.SubscribeAsync<LockReleased>(message => received.TrySetResult(message), TestCancellationToken);
        await bus.PublishAsync(new LockReleased("orders"), cancellationToken: TestCancellationToken);

        Assert.Equal("orders", (await received.Task.WaitAsync(TimeSpan.FromSeconds(10), TestCancellationToken)).Resource);
        Assert.Empty(queues.Sends);
        Assert.Empty(queues.Receivers);
        Assert.Equal(MessageDestinationKind.Topic, Assert.Single(topics.Sends).Kind);
        Assert.Equal(MessageSourceKind.InstanceSubscription, Assert.Single(topics.Receivers).Kind);
    }

    [Fact]
    public async Task MessagingStatus_WhenOnlyTheTopicTransportFails_ReportsTheErrorOnTheSubscriptionsOnly()
    {
        await using var queues = new RecordedTransport();
        await using var topics = new RecordedTransport { FailReceivers = true };
        await using var host = await StartAsync(queues, topics, m => m
            .AddConsumer<PlaceOrder>((_, _, _) => Task.CompletedTask)
            .AddSubscriber<OrderPlaced>((_, _, _) => Task.CompletedTask));
        var status = host.Services.GetRequiredService<MessagingStatus>();

        await WaitForAsync(() => status.GetProcessors().Any(p => p.LastError is not null));

        var processors = status.GetProcessors().ToDictionary(p => p.Name);
        Assert.Equal((ProcessingState.Running, null), (processors["queue:split.place-order"].State, processors["queue:split.place-order"].LastError));
        Assert.Contains("topics are down", processors["subscription:split.order-placed/split"].LastError);
    }

    [Fact]
    public async Task WorkMonitorBacklog_WithTopicsOnTheirOwnTransport_CountsEachSourceOnItsTransport()
    {
        await using var queues = new RecordedTransport();
        await using var topics = new RecordedTransport();
        await using var host = await StartAsync(queues, topics, m => m
            .AddConsumer<PlaceOrder>((_, _, _) => Task.CompletedTask)
            .AddSubscriber<OrderPlaced>((_, _, _) => Task.CompletedTask), s => s.AddFoundatioMonitoring());
        await WaitForAsync(() => host.Services.GetRequiredService<MessagingStatus>().GetProcessors().All(p => p.State == ProcessingState.Running));

        var backlog = await host.Services.GetRequiredService<IWorkMonitor>().GetBacklogAsync(TestCancellationToken);

        Assert.Equal(["queue:split.place-order", "subscription:split.order-placed/split"], backlog.Select(b => b.Source.ToString()).Order(StringComparer.Ordinal));
        Assert.Equal([MessageSource.Queue("split.place-order")], queues.Stats);
        Assert.Equal([MessageSource.Subscription("split.order-placed", "split")], topics.Stats);
    }

    [Fact]
    public async Task Telemetry_WithTopicsOnTheirOwnTransport_NamesTheSystemOfEachTransport()
    {
        var measurements = new ConcurrentQueue<(string Instrument, string? System, string? Destination)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == FoundatioDiagnostics.Meter.Name)
                    l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) => measurements.Enqueue((instrument.Name, Tag(tags, "messaging.system"), Tag(tags, "messaging.destination.name"))));
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) => measurements.Enqueue((instrument.Name, Tag(tags, "messaging.system"), Tag(tags, "messaging.destination.name"))));
        listener.Start();

        await using var queues = new InMemoryMessageTransport();
        await using var topics = new RecordedTransport();
        var handled = new ConcurrentQueue<string>();
        await using var host = await MessagingTestHost.StartAsync(queues, Log, "split", m => m
            .ForTopics(t => t.UseTransport(topics.Transport))
            .MapMessage<PlaceOrder>("split.telemetry-order")
            .MapMessage<OrderPlaced>("split.telemetry-placed")
            .AddConsumer<PlaceOrder>((_, _, _) => Record(handled, "consumer"))
            .AddSubscriber<OrderPlaced>((_, _, _) => Record(handled, "subscriber")));

        await host.Client.SendAsync(new PlaceOrder(1), cancellationToken: TestCancellationToken);
        await host.Client.PublishAsync(new OrderPlaced(1), cancellationToken: TestCancellationToken);
        await WaitForAsync(() => handled.Count == 2);
        await WaitForAsync(() => measurements.Count(m => m.Instrument == "messaging.process.duration" && m.Destination is "split.telemetry-order" or "split.telemetry-placed") >= 2);

        var systems = measurements.Where(m => m.Destination is "split.telemetry-order" or "split.telemetry-placed")
            .Select(m => (m.Instrument, m.Destination, m.System)).Distinct().ToList();
        Assert.Contains(("messaging.client.sent.messages", "split.telemetry-order", "inmemory"), systems);
        Assert.Contains(("messaging.client.sent.messages", "split.telemetry-placed", "intercepting"), systems);
        Assert.Contains(("messaging.process.duration", "split.telemetry-order", "inmemory"), systems);
        Assert.Contains(("messaging.process.duration", "split.telemetry-placed", "intercepting"), systems);
        Assert.DoesNotContain(systems, s => s.Destination == "split.telemetry-order" && s.System != "inmemory");
        Assert.DoesNotContain(systems, s => s.Destination == "split.telemetry-placed" && s.System != "intercepting");
    }

    [Fact]
    public void ForTopics_WithHandlersOrSettingsInside_Throws()
    {
        var services = new ServiceCollection();
        Action<FoundatioMessagingBuilder>[] calls =
        [
            t => t.AddConsumer<PlaceOrder>((_, _, _) => Task.CompletedTask),
            t => t.AddSubscriber<OrderPlaced>((_, _, _) => Task.CompletedTask),
            t => t.MapMessage<OrderPlaced>("split.other"),
            t => t.UseServiceName("other"),
            t => t.Configure(_ => { }),
            t => t.AddLegacyMessageBus(),
            t => t.DecorateClient(inner => inner),
            t => t.ForTopics(x => x.UseInMemory())
        ];

        foreach (var call in calls)
        {
            var error = Assert.Throws<InvalidOperationException>(() => services.AddFoundatioMessaging(m => m.UseInMemory().ForTopics(t =>
            {
                t.UseInMemory();
                call(t);
            })));
            Assert.Contains("ForTopics", error.Message);
        }
    }

    [Fact]
    public void ForTopics_WithoutATransport_Throws()
    {
        var error = Assert.Throws<ArgumentException>(() => new ServiceCollection().AddFoundatioMessaging(m => m.UseInMemory().ForTopics(_ => { })));

        Assert.Contains("UseRedis()", error.Message);
    }

    [Fact]
    public async Task Dispose_WithTransportsTheProviderCreated_DisposesEachOnce()
    {
        var queues = new InterceptingMessageTransport(new InMemoryMessageTransport());
        var topics = new InterceptingMessageTransport(new InMemoryMessageTransport());
        var services = new ServiceCollection();
        services.AddFoundatioMessaging(m => m.UseTransport(_ => queues).ForTopics(t => t.UseTransport(_ => topics)).UseServiceName("split"));

        await using (var provider = services.BuildServiceProvider())
            provider.GetRequiredService<IMessageClient>();

        Assert.Equal((1, 1), (queues.DisposeCalls, topics.DisposeCalls));
    }

    [Fact]
    public async Task Dispose_WithTransportsPassedAsInstances_DisposesNeither()
    {
        var queues = new InterceptingMessageTransport(new InMemoryMessageTransport());
        var topics = new InterceptingMessageTransport(new InMemoryMessageTransport());
        var services = new ServiceCollection();
        services.AddFoundatioMessaging(m => m.UseTransport(queues).ForTopics(t => t.UseTransport(topics)).UseServiceName("split"));

        await using (var provider = services.BuildServiceProvider())
            provider.GetRequiredService<IMessageClient>();

        Assert.Equal((0, 0), (queues.DisposeCalls, topics.DisposeCalls));
    }

    [Fact]
    public void UnnamedServiceSubscriber_WithTopicsOnTheInMemoryTransport_IsNamedAfterTheApplication()
    {
        var services = new ServiceCollection();
        services.AddFoundatioMessaging(m => m
            .UseTransport(new InterceptingMessageTransport(new InMemoryMessageTransport()))
            .ForTopics(t => t.UseInMemory())
            .AddSubscriber<OrderPlaced>((_, _, _) => Task.CompletedTask));
        using var provider = services.BuildServiceProvider();

        var source = Assert.Single(provider.GetRequiredService<IMessageAdmin>().GetSources());

        Assert.Equal(MessageSourceKind.Subscription, source.Kind);
    }

    [Fact]
    public async Task UnnamedServiceSubscriber_WithTopicsOnABrokerTransport_RequiresAServiceName()
    {
        var services = new ServiceCollection();
        services.AddFoundatioMessaging(m => m
            .UseInMemory()
            .ForTopics(t => t.UseTransport(new InterceptingMessageTransport(new InMemoryMessageTransport())))
            .AddSubscriber<OrderPlaced>((_, _, _) => Task.CompletedTask));
        await using var provider = services.BuildServiceProvider();

        var error = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IMessageClient>());

        Assert.Contains("InterceptingMessageTransport keeps them", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TestHarness_WithTopicsOnTheirOwnTransport_CarriesBothKindsOnTheHarnessTransport(bool harnessFirst)
    {
        await using var topics = new RecordedTransport();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ILoggerFactory>(Log);
        if (harnessFirst)
            builder.Services.AddFoundatioTestHarness();
        builder.Services.AddFoundatioMessaging(m => m
            .UseInMemory()
            .ForTopics(t => t.UseTransport(topics.Transport))
            .AddConsumer<PlaceOrder>((_, _, _) => Task.CompletedTask)
            .AddSubscriber<OrderPlaced>((_, _, _) => Task.CompletedTask));
        if (!harnessFirst)
            builder.Services.AddFoundatioTestHarness();
        using var host = builder.Build();
        await host.StartAsync(TestCancellationToken);
        var harness = host.Services.GetRequiredService<FoundatioTestHarness>();

        await host.Services.GetRequiredService<IMessageClient>().SendAsync(new PlaceOrder(1), cancellationToken: TestCancellationToken);
        await host.Services.GetRequiredService<IMessageClient>().PublishAsync(new OrderPlaced(1), cancellationToken: TestCancellationToken);
        await harness.WaitForIdleAsync(cancellationToken: TestCancellationToken);
        await host.StopAsync(TestCancellationToken);

        Assert.Equal(2, harness.HandledMessages.Count(m => m.Outcome == MessageOutcome.Completed));
        Assert.Empty(topics.Sends);
        Assert.Empty(topics.Receivers);
    }

    private Task<MessagingTestHost> StartAsync(RecordedTransport queues, RecordedTransport topics, Action<FoundatioMessagingBuilder> configure, Action<IServiceCollection>? services = null)
        => MessagingTestHost.StartAsync(queues.Transport, Log, "split", m => configure(m.ForTopics(t => t.UseTransport(topics.Transport))), services);

    private static Task Record(ConcurrentQueue<string> handled, string handler)
    {
        handled.Enqueue(handler);
        return Task.CompletedTask;
    }

    private static Task FailFirst(ConcurrentQueue<(string, int)> attempts, string handler, IMessageContext context)
    {
        attempts.Enqueue((handler, context.Attempt));
        return context.Attempt == 1 ? throw new InvalidOperationException("first attempt") : Task.CompletedTask;
    }

    private static string? Tag(ReadOnlySpan<KeyValuePair<string, object?>> tags, string name)
    {
        foreach (var tag in tags)
        {
            if (tag.Key == name)
                return tag.Value as string;
        }

        return null;
    }

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

    [Message("split.place-order")]
    public record PlaceOrder(int OrderId, string? Notes = null);

    [Message("split.order-placed")]
    public record OrderPlaced(int OrderId, string? Notes = null);

    public record LockReleased(string Resource);

    /// <summary>An in-memory transport that records what each kind of traffic did on it.</summary>
    private sealed class RecordedTransport : IAsyncDisposable
    {
        public RecordedTransport(TransportCapabilities? capabilities = null)
        {
            Inner = capabilities is null ? new InMemoryMessageTransport() : new InMemoryMessageTransport(capabilities);
            Transport = new InterceptingMessageTransport(Inner)
            {
                OnSend = (destination, messages) => Sends.Enqueue(destination),
                OnCreateReceiver = (source, _) =>
                {
                    Receivers.Enqueue(source);
                    return FailReceivers ? Task.FromException(new InvalidOperationException("The topics are down.")) : Task.CompletedTask;
                },
                OnSettle = (operation, _) => Settles.Enqueue(operation),
                OnGetStats = source => Stats.Enqueue(source),
                OnDelete = source => Deletes.Enqueue(source)
            };
        }

        public InMemoryMessageTransport Inner { get; }
        public InterceptingMessageTransport Transport { get; }
        public bool FailReceivers { get; init; }
        public ConcurrentQueue<MessageDestination> Sends { get; } = new();
        public ConcurrentQueue<MessageSource> Receivers { get; } = new();
        public ConcurrentQueue<string> Settles { get; } = new();
        public ConcurrentQueue<MessageSource> Stats { get; } = new();
        public ConcurrentQueue<MessageSource> Deletes { get; } = new();

        public ValueTask DisposeAsync() => Inner.DisposeAsync();
    }
}
