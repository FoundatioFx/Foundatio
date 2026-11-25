using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Foundatio.Testing;
using Foundatio.Xunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;
using static Foundatio.Tests.Testing.OrdersApp;

namespace Foundatio.Tests.Messaging;

public class MessageClientDecoratorTests : TestWithLoggingBase
{
    private readonly ITestOutputHelper _output;

    public MessageClientDecoratorTests(ITestOutputHelper output) : base(output)
    {
        _output = output;
    }

    [Fact]
    public async Task Decorator_OverridingOnlyPublishAsync_PassesEveryOtherCallAndOverloadToTheInnerClient()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders().AddTestLogger(_output);
        builder.Services.AddFoundatioMessaging(m => m.UseServiceName("orders"));
        builder.Services.AddFoundatioTestHarness(o => o.StartPaused = true);
        using var host = builder.Build();
        await host.StartAsync(TestCancellationToken);
        var harness = host.Services.GetRequiredService<FoundatioTestHarness>();
        var client = new FailFirstPublish(host.Services.GetRequiredService<IMessageClient>());

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.PublishAsync(new OrderCancelled(1), cancellationToken: TestCancellationToken));
        string published = await client.PublishAsync(new OrderCancelled(2), cancellationToken: TestCancellationToken);
        string sent = await client.SendAsync(new ShipOrder(3), cancellationToken: TestCancellationToken);
        var batch = await client.SendBatchAsync([new ShipOrder(4), new ShipOrder(5)], cancellationToken: TestCancellationToken);
        var keyed = await client.SendBatchAsync([new ShipOrder(6)], o => new MessageKeys { MessageId = $"ship-{o.OrderId}" }, cancellationToken: TestCancellationToken);
        var publishedBatch = await client.PublishBatchAsync([new OrderCancelled(7)], new PublishOptions { CorrelationId = "c-7" }, TestCancellationToken);
        var keyedPublish = await client.PublishBatchAsync([new OrderCancelled(8)], o => new MessageKeys { MessageId = $"cancel-{o.OrderId}" }, cancellationToken: TestCancellationToken);

        Assert.Equal(2, client.Publishes);
        Assert.Equal([3, 4, 5, 6], harness.Sent<ShipOrder>().Select(o => o.OrderId));
        Assert.Equal([sent, .. batch, "ship-6"], harness.SentMessages.Select(m => m.MessageId));
        Assert.Equal(["ship-6"], keyed);
        Assert.Equal([2, 7, 8], harness.Published<OrderCancelled>().Select(o => o.OrderId));
        Assert.Equal([published, .. publishedBatch, "cancel-8"], harness.PublishedMessages.Select(m => m.MessageId));
        Assert.Equal(["cancel-8"], keyedPublish);
        Assert.Equal("c-7", harness.PublishedMessages[1].CorrelationId);
        await host.StopAsync(TestCancellationToken);
    }

    [Fact]
    public async Task DecorateClient_GivesHandlersTheDecoratedClient_InTheOrderTheyWereAdded()
    {
        var calls = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders().AddTestLogger(_output);
        builder.Services.AddSingleton(calls);
        builder.Services.AddFoundatioMessaging(m => m
            .UseServiceName("orders")
            .AddConsumer<ShipOrder>(async (order, context, ct) => await context.Services.GetRequiredService<IMessageClient>().PublishAsync(new OrderCancelled(order.OrderId), cancellationToken: ct))
            .DecorateClient<FailFirstPublish>()
            .DecorateClient(inner => new RecordingClient(inner, calls)));
        builder.Services.AddFoundatioTestHarness(o => o.RetryDelay = TimeSpan.Zero);
        using var host = builder.Build();
        await host.StartAsync(TestCancellationToken);
        var harness = host.Services.GetRequiredService<FoundatioTestHarness>();

        var client = Assert.IsType<RecordingClient>(host.Services.GetRequiredService<IMessageClient>());
        await client.SendAsync(new ShipOrder(1), cancellationToken: TestCancellationToken);
        await harness.WaitForIdleAsync(TimeSpan.FromSeconds(10), TestCancellationToken);

        Assert.Equal([MessageOutcome.Retried, MessageOutcome.Completed], harness.HandledMessages.Select(m => m.Outcome));
        Assert.Equal([1], harness.Published<OrderCancelled>().Select(o => o.OrderId));
        Assert.Equal(["send", "publish", "publish"], calls);
        await host.StopAsync(TestCancellationToken);
    }

    private sealed class RecordingClient(IMessageClient inner, System.Collections.Concurrent.ConcurrentQueue<string> calls) : MessageClientDecorator(inner)
    {
        public override Task<string> SendAsync<T>(T message, SendOptions? options = null, CancellationToken cancellationToken = default)
        {
            calls.Enqueue("send");
            return base.SendAsync(message, options, cancellationToken);
        }

        public override Task<string> PublishAsync<T>(T message, PublishOptions? options = null, CancellationToken cancellationToken = default)
        {
            calls.Enqueue("publish");
            return base.PublishAsync(message, options, cancellationToken);
        }
    }

    private sealed class FailFirstPublish(IMessageClient inner) : MessageClientDecorator(inner)
    {
        private int _publishes;

        public int Publishes => _publishes;

        public override Task<string> PublishAsync<T>(T message, PublishOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _publishes) == 1)
                return Task.FromException<string>(new InvalidOperationException("Simulated publish failure"));

            return base.PublishAsync(message, options, cancellationToken);
        }
    }
}
