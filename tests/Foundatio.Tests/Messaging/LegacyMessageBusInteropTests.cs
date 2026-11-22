using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;
using Foundatio.Testing;
using Foundatio.Xunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Foundatio.Tests.Messaging;

/// <summary>Pub/sub between <c>AddLegacyMessageBus()</c> and <see cref="IMessageClient"/> on one transport.</summary>
public class LegacyMessageBusInteropTests : TestWithLoggingBase
{
    private static readonly TimeSpan NoMoreDeliveries = TimeSpan.FromMilliseconds(300);

    private readonly ITestOutputHelper _output;
    private readonly List<IHost> _hosts = [];
    private readonly List<IMessageTransport> _transports = [];

    public LegacyMessageBusInteropTests(ITestOutputHelper output) : base(output)
    {
        _output = output;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PublishAsync_OfAMappedType_ReachesSubscribersOfBothApisOnce(bool publishWithMessageClient)
    {
        // Arrange
        var host = await StartHostAsync(m => m.MapMessage<InteropEvent>("interop-event"));
        var bus = host.Services.GetRequiredService<IMessageBus>();
        var client = host.Services.GetRequiredService<IMessageClient>();
        var legacy = new Counter();
        var v2 = new Counter();
        await bus.SubscribeAsync<InteropEvent>(_ => legacy.Increment(), TestCancellationToken);
        await using var subscription = await client.SubscribeAsync<InteropEvent>((_, _, _) => v2.IncrementAsync(), cancellationToken: TestCancellationToken);

        // Act
        if (publishWithMessageClient)
            await client.PublishAsync(new InteropEvent(1), cancellationToken: TestCancellationToken);
        else
            await bus.PublishAsync(new InteropEvent(1), cancellationToken: TestCancellationToken);

        // Assert
        await legacy.WaitForAsync(1, TestCancellationToken);
        await v2.WaitForAsync(1, TestCancellationToken);
        await Task.Delay(NoMoreDeliveries, TestCancellationToken);
        Assert.Equal(1, legacy.Count);
        Assert.Equal(1, v2.Count);
    }

    [Fact]
    public async Task PublishAsync_OfATypeWithARegisteredSubscriber_ReachesTheOtherApi()
    {
        // Arrange
        var v2 = new Counter();
        var host = await StartHostAsync(m => m.AddSubscriber<InteropEvent>((_, _, _) => v2.IncrementAsync()));
        var bus = host.Services.GetRequiredService<IMessageBus>();
        var legacy = new Counter();
        await bus.SubscribeAsync<InteropEvent>(_ => legacy.Increment(), TestCancellationToken);

        // Act
        await bus.PublishAsync(new InteropEvent(1), cancellationToken: TestCancellationToken);
        await host.Services.GetRequiredService<IMessageClient>().PublishAsync(new InteropEvent(2), cancellationToken: TestCancellationToken);

        // Assert
        await legacy.WaitForAsync(2, TestCancellationToken);
        await v2.WaitForAsync(2, TestCancellationToken);
        await Task.Delay(NoMoreDeliveries, TestCancellationToken);
        Assert.Equal(2, legacy.Count);
        Assert.Equal(2, v2.Count);
    }

    [Fact]
    public async Task PublishAsync_BetweenServicesThatMapTheType_ReachesTheOtherApi()
    {
        // Arrange
        var transport = new InMemoryMessageTransport();
        _transports.Add(transport);
        var v2Handled = new Counter();
        var legacyService = await StartHostAsync(m => m.UseTransport(transport).UseServiceName("legacy").MapMessage<InteropEvent>("interop-event"));
        var v2Service = await StartHostAsync(m => m.UseTransport(transport).UseServiceName("modern").MapMessage<InteropEvent>("interop-event")
            .AddSubscriber<InteropEvent>((_, _, _) => v2Handled.IncrementAsync()));
        var legacyHandled = new Counter();
        await legacyService.Services.GetRequiredService<IMessageBus>().SubscribeAsync<InteropEvent>(_ => legacyHandled.Increment(), TestCancellationToken);

        // Act
        await legacyService.Services.GetRequiredService<IMessageBus>().PublishAsync(new InteropEvent(1), cancellationToken: TestCancellationToken);
        await v2Service.Services.GetRequiredService<IMessageClient>().PublishAsync(new InteropEvent(2), cancellationToken: TestCancellationToken);

        // Assert
        await v2Handled.WaitForAsync(2, TestCancellationToken);
        await legacyHandled.WaitForAsync(2, TestCancellationToken);
        await Task.Delay(NoMoreDeliveries, TestCancellationToken);
        Assert.Equal(2, v2Handled.Count);
        Assert.Equal(2, legacyHandled.Count);
    }

    [Fact]
    public async Task PublishAsync_OfATypeMessagingDoesNotKnow_OnlyPublishesToTheLegacyTopic()
    {
        // Arrange
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders().AddTestLogger(_output);
        builder.Services.AddFoundatioTestHarness();
        builder.Services.AddFoundatioMessaging(m => m.UseInMemory().MapMessage<InteropEvent>("interop-event").AddLegacyMessageBus());
        var host = await StartAsync(builder);
        var harness = host.Services.GetRequiredService<FoundatioTestHarness>();
        var bus = host.Services.GetRequiredService<IMessageBus>();
        var legacy = new Counter();
        await bus.SubscribeAsync<LegacyOnlyEvent>(_ => legacy.Increment(), TestCancellationToken);

        // Act
        await bus.PublishAsync(new LegacyOnlyEvent(1), cancellationToken: TestCancellationToken);
        await bus.PublishAsync(new InteropEvent(2), cancellationToken: TestCancellationToken);

        // Assert
        await legacy.WaitForAsync(1, TestCancellationToken);
        var published = Assert.Single(harness.PublishedMessages);
        Assert.Equal("interop-event", published.MessageType);
        Assert.Equal("topic:interop-event", published.Address);
        Assert.Equal(new InteropEvent(2), published.Message);
    }

    public override async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            await host.StopAsync(CancellationToken.None);
            host.Dispose();
        }

        foreach (var transport in _transports)
            await transport.DisposeAsync();

        await base.DisposeAsync();
    }

    private Task<IHost> StartHostAsync(Action<FoundatioMessagingBuilder> configure)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders().AddTestLogger(_output);
        builder.Services.AddFoundatioMessaging(m =>
        {
            m.UseInMemory().UseServiceName("interop").AddLegacyMessageBus();
            configure(m);
        });
        return StartAsync(builder);
    }

    private async Task<IHost> StartAsync(HostApplicationBuilder builder)
    {
        var host = builder.Build();
        _hosts.Add(host);
        await host.StartAsync(TestCancellationToken);
        return host;
    }

    public record InteropEvent(int Id);

    public record LegacyOnlyEvent(int Id);

    private sealed class Counter
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Increment() => Interlocked.Increment(ref _count);

        public Task IncrementAsync()
        {
            Increment();
            return Task.CompletedTask;
        }

        public async Task WaitForAsync(int count, CancellationToken cancellationToken)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (Count < count)
            {
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException($"Received {Count} of {count} messages.");

                await Task.Delay(20, cancellationToken);
            }
        }
    }
}
