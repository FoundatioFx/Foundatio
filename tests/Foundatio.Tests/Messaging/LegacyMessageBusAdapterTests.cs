using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.AsyncEx;
using Foundatio.Messaging;
using Foundatio.Messaging.Legacy;
using Foundatio.Tests.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using IMessageBus = Foundatio.Messaging.IMessageBus;

namespace Foundatio.Tests.Messaging;

public class LegacyMessageBusAdapterTests
{
    [Fact]
    public async Task OldStyleSubscribeAndPublish_WorkOverTheNewBusAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var bus = new MessageBus(new InMemoryMessageTransport());
        await using var adapter = new LegacyMessageBusAdapter(bus);
        var received = new AsyncCountdownEvent(2);
        var payloads = new ConcurrentQueue<string?>();

        // Two old-style subscribers: BOTH must receive every published message (the old fan-out semantics).
        await adapter.SubscribeAsync<LegacyEvent>((message, _) =>
        {
            payloads.Enqueue(message.Data);
            received.Signal();
            return Task.CompletedTask;
        }, cancellationToken);

        await adapter.SubscribeAsync<LegacyEvent>((message, _) =>
        {
            payloads.Enqueue(message.Data);
            received.Signal();
            return Task.CompletedTask;
        }, cancellationToken);

        // Old-style publish extension with MessageOptions.
        await adapter.PublishAsync(new LegacyEvent { Data = "hello" }, new MessageOptions { CorrelationId = "abc" }, cancellationToken);

        await received.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, payloads.Count);
        Assert.All(payloads, data => Assert.Equal("hello", data));
    }

    [Fact]
    public async Task NewBusSubscribers_ReceiveAdapterPublishesAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var bus = new MessageBus(new InMemoryMessageTransport());
        await using var adapter = new LegacyMessageBusAdapter(bus);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        var received = new AsyncCountdownEvent(1);

        // Migrated code on the NEW api and unmigrated code on the adapter interoperate: same bus, same topics.
        await using var subscription = await bus.SubscribeAsync<LegacyEvent>((context, _) =>
        {
            Assert.Equal("bridged", context.Message.Data);
            received.Signal();
            return Task.CompletedTask;
        }, cancellationToken: cts.Token);

        await adapter.PublishAsync(new LegacyEvent { Data = "bridged" }, cancellationToken: cancellationToken);
        await received.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task AddLegacyAdapter_ResolvesOldInterfacesFromDiAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var services = new ServiceCollection();
        services.AddFoundatio()
            .Messaging.UseInMemory()
            .Messaging.AddLegacyAdapter();

        await using var provider = services.BuildServiceProvider();
        var legacyBus = provider.GetRequiredService<Foundatio.Messaging.Legacy.IMessageBus>();
        Assert.IsType<LegacyMessageBusAdapter>(legacyBus);
        Assert.Same(legacyBus, provider.GetRequiredService<IMessagePublisher>());
        Assert.Same(legacyBus, provider.GetRequiredService<IMessageSubscriber>());

        var received = new AsyncCountdownEvent(1);
        await legacyBus.SubscribeAsync<LegacyEvent>((message, _) =>
        {
            received.Signal();
            return Task.CompletedTask;
        }, cancellationToken);

        // The adapter and the new bus resolved from the SAME container share the transport.
        await provider.GetRequiredService<IMessageBus>().PublishAsync(new LegacyEvent { Data = "di" }, cancellationToken: cancellationToken);
        await received.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private sealed class LegacyEvent
    {
        public string? Data { get; set; }
    }
}
