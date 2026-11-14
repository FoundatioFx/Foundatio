using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;
using Foundatio.Xunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using StackExchange.Redis;
using Xunit;

namespace Foundatio.Redis.Tests.Messaging;

public class RedisMessagingBuilderExtensionsTests(ITestOutputHelper output) : TestWithLoggingBase(output)
{
    [Fact]
    public async Task UseRedis_WithARegisteredConnection_DeliversToAConsumerAndDisposesTheTransport()
    {
        // Arrange
        var muxer = SharedConnection.GetMuxer(Log);
        Assert.SkipWhen(muxer is null, "RedisConnectionString is not configured.");
        var received = new TaskCompletionSource<Ping>(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(Log);
        services.AddFoundatioMessaging(m => m
            .UseRedis(new RedisMessageTransportOptions { Prefix = $"foundatio-tests:helpers-{Guid.NewGuid():N}" })
            .UseServiceName("helpers")
            .AddConsumer<Ping>((ping, _, _) =>
            {
                received.TrySetResult(ping);
                return Task.CompletedTask;
            }));
        services.AddSingleton<IConnectionMultiplexer>(muxer);
        var provider = services.BuildServiceProvider();
        var hostedServices = provider.GetServices<IHostedService>().ToList();
        foreach (var hostedService in hostedServices)
            await hostedService.StartAsync(TestCancellationToken);

        // Act
        await provider.GetRequiredService<IMessageClient>().SendAsync(new Ping("hello"), cancellationToken: TestCancellationToken);
        var ping = await received.Task.WaitAsync(TimeSpan.FromSeconds(30), TestCancellationToken);
        var transport = Assert.IsType<RedisMessageTransport>(provider.GetRequiredService<IMessageTransport>());
        foreach (var hostedService in Enumerable.Reverse(hostedServices))
            await hostedService.StopAsync(CancellationToken.None);
        await provider.DisposeAsync();

        // Assert
        Assert.Equal("hello", ping.Text);
        Assert.Same(Log, transport.Options.LoggerFactory);
        Assert.True(transport.IsDisposed);
    }

    [Fact]
    public async Task UseRedis_WithAConnectionAndOptions_KeepsWhatTheOptionsSetAndFillsTheRestFromServices()
    {
        // Arrange
        var muxer = SharedConnection.GetMuxer(Log);
        Assert.SkipWhen(muxer is null, "RedisConnectionString is not configured.");
        var timeProvider = new FakeTimeProvider();
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(Log);
        services.AddSingleton<TimeProvider>(timeProvider);
        services.AddFoundatioMessaging(m => m.UseRedis(muxer, new RedisMessageTransportOptions { Prefix = "foundatio-tests:helpers", LoggerFactory = NullLoggerFactory.Instance }));
        await using var provider = services.BuildServiceProvider();

        // Act
        var transport = Assert.IsType<RedisMessageTransport>(provider.GetRequiredService<IMessageTransport>());

        // Assert
        Assert.Equal("foundatio-tests:helpers", transport.Options.Prefix);
        Assert.Same(NullLoggerFactory.Instance, transport.Options.LoggerFactory);
        Assert.Same(timeProvider, transport.TimeProvider);
    }

    [Fact]
    public async Task UseRedis_WithoutARegisteredConnection_ThrowsWhenTheTransportIsCreated()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddFoundatioMessaging(m => m.UseRedis(_ => new RedisMessageTransportOptions()));
        await using var provider = services.BuildServiceProvider();

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IMessageTransport>());

        // Assert
        Assert.Contains(nameof(IConnectionMultiplexer), exception.Message);
    }

    public record Ping(string Text);
}
