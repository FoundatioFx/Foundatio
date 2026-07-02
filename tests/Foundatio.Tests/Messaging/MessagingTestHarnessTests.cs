using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Foundatio.Messaging.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
