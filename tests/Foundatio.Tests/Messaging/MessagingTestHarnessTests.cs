using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Foundatio.Messaging.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Foundatio.Tests.Messaging;

public class MessagingTestHarnessTests
{
    [Fact]
    public async Task Harness_RecordsSendPublishAndHandledWithTypedAccessAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = new MessagingTestHarness();
        await using var bus = new MessageBus(harness.Transport, new MessageBusOptions { OwnsTransport = false });

        var handled = new List<string>();
        await using var subscription = await bus.SubscribeAsync<HarnessOrder>((context, _) =>
        {
            lock (handled)
                handled.Add(context.Message.Id);
            return Task.CompletedTask;
        }, cancellationToken: cancellationToken);

        await bus.SendAsync(new HarnessOrder { Id = "cmd" }, cancellationToken: cancellationToken);
        await bus.PublishAsync(new HarnessOrder { Id = "evt" }, cancellationToken: cancellationToken);
        await harness.WaitForIdleAsync(cancellationToken: cancellationToken);

        // Sends and publishes are recorded separately, deserialized back to the message type.
        Assert.Equal("cmd", Assert.Single(harness.Sent<HarnessOrder>()).Id);
        Assert.Equal("evt", Assert.Single(harness.Published<HarnessOrder>()).Id);
        Assert.Equal(2, harness.Handled<HarnessOrder>().Count);
        Assert.Equal(2, handled.Count);

        // Raw recordings carry the route and role for topology assertions.
        var sent = Assert.Single(harness.SentMessages);
        Assert.Equal("harness-orders", sent.Destination);
        Assert.Equal(DestinationRole.Queue, sent.Role);
        var published = Assert.Single(harness.PublishedMessages);
        Assert.Equal("harness-orders", published.Destination);
        Assert.Equal(DestinationRole.Topic, published.Role);

        // Negative assertions are immediate once idle.
        Assert.Empty(harness.DeadLetteredMessages);
        Assert.Empty(harness.AbandonedMessages);
        Assert.Empty(harness.Sent<HarnessOther>());
    }

    [Fact]
    public async Task Harness_RetryCycleEndsInDeadLetterAndIsFullyObservableAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = new MessagingTestHarness();
        await using var bus = new MessageBus(harness.Transport, new MessageBusOptions { OwnsTransport = false });

        int attempts = 0;
        await using var subscription = await bus.SubscribeAsync<HarnessOrder>((_, _) =>
        {
            Interlocked.Increment(ref attempts);
            throw new InvalidOperationException("always fails");
        }, new MessageSubscriptionOptions { MaxAttempts = 3, RedeliveryBackoff = _ => TimeSpan.Zero }, cancellationToken);

        await bus.SendAsync(new HarnessOrder { Id = "poison" }, cancellationToken: cancellationToken);
        await harness.WaitForIdleAsync(cancellationToken: cancellationToken);

        // The whole failure path is assertable: two retries, then terminal dead-letter with the reason and forensics.
        Assert.Equal(3, Volatile.Read(ref attempts));
        Assert.Equal(2, harness.AbandonedMessages.Count);
        Assert.All(harness.Abandoned<HarnessOrder>(), m => Assert.Equal("poison", m.Id));
        Assert.Equal(2, harness.Abandoned<HarnessOrder>().Count);
        var dead = Assert.Single(harness.DeadLetteredMessages);
        Assert.Equal("handler-error", dead.Reason);
        Assert.Equal(3, dead.Attempts);
        Assert.Equal(typeof(InvalidOperationException).FullName, dead.Headers[KnownHeaders.DeadLetterExceptionType]);
        Assert.Equal("poison", Assert.Single(harness.DeadLettered<HarnessOrder>()).Id);
        Assert.Empty(harness.HandledMessages);
    }

    [Fact]
    public async Task WaitForIdle_CoversDelayedRedeliveriesAndTimesOutWithDiagnosticsAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = new MessagingTestHarness();
        await using var bus = new MessageBus(harness.Transport, new MessageBusOptions { OwnsTransport = false });

        int attempts = 0;
        await using var subscription = await bus.SubscribeAsync<HarnessOrder>((_, _) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
                throw new InvalidOperationException("fails once");
            return Task.CompletedTask;
        }, new MessageSubscriptionOptions { MaxAttempts = 2, RedeliveryBackoff = _ => TimeSpan.FromMilliseconds(500) }, cancellationToken);

        await bus.SendAsync(new HarnessOrder { Id = "retry-me" }, cancellationToken: cancellationToken);

        // The retry is parked in a redelivery timer (neither queued nor in flight); the harness must still see it.
        await harness.WaitForIdleAsync(cancellationToken: cancellationToken);
        Assert.Equal(2, Volatile.Read(ref attempts));
        Assert.Single(harness.Handled<HarnessOrder>());

        // Timeout.InfiniteTimeSpan means wait-until-idle; other negative timeouts are rejected up front.
        await harness.WaitForIdleAsync(Timeout.InfiniteTimeSpan, cancellationToken);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => harness.WaitForIdleAsync(TimeSpan.FromMilliseconds(-2), cancellationToken));

        // A destination that never drains fails with the busy destinations named.
        await using var stuck = await bus.SubscribeAsync<HarnessOther>((_, handlerToken) => Task.Delay(Timeout.Infinite, handlerToken),
            cancellationToken: cancellationToken);
        await bus.SendAsync(new HarnessOther { Id = "stuck" }, cancellationToken: cancellationToken);

        var timeout = await Assert.ThrowsAsync<TimeoutException>(() => harness.WaitForIdleAsync(TimeSpan.FromSeconds(2), cancellationToken));
        Assert.Contains("harness-other", timeout.Message);
    }

    [Fact]
    public async Task WaitForHandled_ReturnsMatchesAndTimesOutWithDiagnosticsAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = new MessagingTestHarness();
        await using var bus = new MessageBus(harness.Transport, new MessageBusOptions { OwnsTransport = false });

        await using var subscription = await bus.SubscribeAsync<HarnessOrder>((_, _) => Task.CompletedTask, cancellationToken: cancellationToken);

        await bus.SendAsync(new HarnessOrder { Id = "one" }, cancellationToken: cancellationToken);
        await bus.SendAsync(new HarnessOrder { Id = "two" }, cancellationToken: cancellationToken);

        // Awaits just the outcome under test — no full-bus drain needed before asserting.
        var handled = await harness.WaitForHandledAsync<HarnessOrder>(2, cancellationToken: cancellationToken);
        Assert.Equal(2, handled.Count);
        Assert.Contains(handled, m => m.Id == "one");
        Assert.Contains(handled, m => m.Id == "two");

        // A type that never settles fails fast, naming everything that WAS recorded.
        var timeout = await Assert.ThrowsAsync<TimeoutException>(() =>
            harness.WaitForHandledAsync<HarnessOther>(timeout: TimeSpan.FromMilliseconds(200), cancellationToken: cancellationToken));
        Assert.Contains("sent=2", timeout.Message);
        Assert.Contains("handled=2", timeout.Message);
        Assert.Contains("deadLettered=0", timeout.Message);
    }

    [Fact]
    public async Task WaitForDeadLettered_WithZeroBackoff_IsSleepFreeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = new MessagingTestHarness();
        await using var bus = new MessageBus(harness.Transport, new MessageBusOptions { OwnsTransport = false });

        // Zero backoff makes the whole retry cycle run without any wall-clock delay — the sleep-free way to test the
        // retry/dead-letter path (no fake clock to advance).
        int attempts = 0;
        await using var subscription = await bus.SubscribeAsync<HarnessOrder>((_, _) =>
        {
            Interlocked.Increment(ref attempts);
            throw new InvalidOperationException("always fails");
        }, new MessageSubscriptionOptions { MaxAttempts = 3, RedeliveryBackoff = _ => TimeSpan.Zero }, cancellationToken);

        await bus.SendAsync(new HarnessOrder { Id = "poison" }, cancellationToken: cancellationToken);

        // The raw records surface the terminal forensics: the reason and the exhausted attempt count.
        var dead = Assert.Single(await harness.WaitForDeadLetteredAsync<HarnessOrder>(cancellationToken: cancellationToken));
        Assert.Equal("handler-error", dead.Reason);
        Assert.Equal(3, dead.Attempts);
        Assert.Equal(3, Volatile.Read(ref attempts));
        Assert.Empty(harness.HandledMessages);
    }

    [Fact]
    public async Task FakeTimeProvider_AdvancingTheClockFiresDelayedRedeliveryAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // With an injected fake TimeProvider the harness still WAITS in real time, but delayed redeliveries execute
        // on the fake clock — the test must advance it itself or the retry never fires.
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var harness = new MessagingTestHarness(timeProvider: timeProvider);
        await using var bus = new MessageBus(harness.Transport, new MessageBusOptions { OwnsTransport = false, TimeProvider = timeProvider });

        int attempts = 0;
        await using var subscription = await bus.SubscribeAsync<HarnessOrder>((_, _) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
                throw new InvalidOperationException("fails once");
            return Task.CompletedTask;
        }, new MessageSubscriptionOptions { MaxAttempts = 2, RedeliveryBackoff = _ => TimeSpan.FromMinutes(5) }, cancellationToken);

        await bus.SendAsync(new HarnessOrder { Id = "clockwork" }, cancellationToken: cancellationToken);

        // Advance only after the failed attempt settles — the redelivery timer is armed by the abandon.
        while (harness.AbandonedMessages.Count == 0)
            await Task.Delay(10, cancellationToken);
        Assert.Empty(harness.HandledMessages);

        timeProvider.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal("clockwork", Assert.Single(await harness.WaitForHandledAsync<HarnessOrder>(cancellationToken: cancellationToken)).Id);
        Assert.Equal(2, Volatile.Read(ref attempts));
    }

    [Fact]
    public async Task DestinationsWithNoConsumer_NamesTheDestinationsNothingConsumesAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = new MessagingTestHarness();
        await using var bus = new MessageBus(harness.Transport, new MessageBusOptions { OwnsTransport = false });

        // The newcomer's first failing test: sent/published fine, idle immediately, Handled empty — because nothing
        // consumes the destination. This property names the culprit.
        await bus.SendAsync(new HarnessOther { Id = "orphan" }, cancellationToken: cancellationToken);
        await bus.PublishAsync(new HarnessOrder { Id = "dropped" }, cancellationToken: cancellationToken);

        Assert.Contains("harness-other", harness.DestinationsWithNoConsumer);
        Assert.Contains("harness-orders", harness.DestinationsWithNoConsumer);

        // Once a subscriber attaches (and drains the parked command), the queue is no longer unconsumed; the topic
        // publish stays listed — it was dropped for having zero subscriptions at publish time.
        await using var subscription = await bus.SubscribeAsync<HarnessOther>((_, _) => Task.CompletedTask, cancellationToken: cancellationToken);
        await harness.WaitForIdleAsync(cancellationToken: cancellationToken);

        Assert.Single(harness.Handled<HarnessOther>());
        Assert.DoesNotContain("harness-other", harness.DestinationsWithNoConsumer);
        Assert.Contains("harness-orders", harness.DestinationsWithNoConsumer);
    }

    [Fact]
    public async Task UseTestHarness_WiresDeclarativeHandlersOverTheRecordingTransportAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFoundatio()
            .Messaging.UseTestHarness()
            .Messaging.AddHandler<HarnessOrder, RecordingOrderHandler>();

        await using var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().ToList();
        foreach (var service in hosted)
            await service.StartAsync(cancellationToken);

        try
        {
            var harness = provider.GetRequiredService<MessagingTestHarness>();
            var bus = provider.GetRequiredService<IMessageBus>();
            Assert.Same(harness.Transport, provider.GetRequiredService<IMessageTransport>());

            await bus.SendAsync(new HarnessOrder { Id = "from-di" }, cancellationToken: cancellationToken);
            await harness.WaitForIdleAsync(cancellationToken: cancellationToken);

            Assert.Equal("from-di", Assert.Single(harness.Sent<HarnessOrder>()).Id);
            Assert.Equal("from-di", Assert.Single(harness.Handled<HarnessOrder>()).Id);
            Assert.Equal("from-di", Assert.Single(RecordingOrderHandler.Handled));
        }
        finally
        {
            foreach (var service in hosted)
                await service.StopAsync(cancellationToken);
        }
    }

    [MessageRoute("harness-orders")]
    public class HarnessOrder { public string Id { get; set; } = ""; }

    [MessageRoute("harness-other")]
    public class HarnessOther { public string Id { get; set; } = ""; }

    private sealed class RecordingOrderHandler : IMessageHandler<HarnessOrder>
    {
        public static readonly List<string> Handled = [];

        public Task HandleAsync(IMessageContext<HarnessOrder> context, CancellationToken cancellationToken)
        {
            lock (Handled)
                Handled.Add(context.Message.Id);
            return Task.CompletedTask;
        }
    }
}
