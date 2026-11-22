using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Jobs.Store;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;
using Foundatio.Tests.Utility;
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
    public async Task UseRedis_WithAConnectionString_RegistersOneConnectionForMessagingAndJobsAndDisposesIt()
    {
        // Arrange
        string? connectionString = Configuration.GetConnectionString("RedisConnectionString");
        Assert.SkipWhen(String.IsNullOrEmpty(connectionString), "RedisConnectionString is not configured.");
        var services = new ServiceCollection();
        services.AddFoundatioMessaging(m => m.UseRedis(connectionString, new RedisMessageTransportOptions { Prefix = "foundatio-tests:helpers" }));
        services.AddFoundatioJobs(j => j.UseRedis(connectionString));
        var provider = services.BuildServiceProvider();

        // Act
        var transport = Assert.IsType<RedisMessageTransport>(provider.GetRequiredService<IMessageTransport>());
        Assert.IsType<RedisJobStore>(provider.GetRequiredService<IJobStore>());
        var connection = Assert.Single(provider.GetServices<IConnectionMultiplexer>());
        bool connected = connection.IsConnected;
        await provider.DisposeAsync();

        // Assert
        Assert.Equal("foundatio-tests:helpers", transport.Options.Prefix);
        Assert.True(connected);
        Assert.False(connection.IsConnected);
    }

    [Fact]
    public void UseRedis_WithAConnectionStringAndAnotherConnection_Throws()
    {
        var registered = new ServiceCollection();
        registered.AddSingleton<IConnectionMultiplexer>(_ => throw new InvalidOperationException("not created"));
        var exception = Assert.Throws<InvalidOperationException>(() => registered.AddFoundatioMessaging(m => m.UseRedis("localhost:6379")));
        Assert.Contains("UseRedis()", exception.Message);

        var other = new ServiceCollection();
        other.AddFoundatioMessaging(m => m.UseRedis("localhost:6379"));
        exception = Assert.Throws<InvalidOperationException>(() => other.AddFoundatioJobs(j => j.UseRedis("localhost:6380")));
        Assert.Contains("connection string", exception.Message);
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

    [Fact]
    public async Task UseRedis_WithAMessagingClock_UsesItOverTheRegisteredOne()
    {
        // Arrange
        var muxer = SharedConnection.GetMuxer(Log);
        Assert.SkipWhen(muxer is null, "RedisConnectionString is not configured.");
        var messagingClock = new FakeTimeProvider();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider());
        services.AddFoundatioMessaging(m => m.UseRedis(muxer).Configure(o => o.TimeProvider = messagingClock));
        await using var provider = services.BuildServiceProvider();

        // Act
        var transport = Assert.IsType<RedisMessageTransport>(provider.GetRequiredService<IMessageTransport>());

        // Assert
        Assert.Same(messagingClock, transport.Options.TimeProvider);
    }

    public record Ping(string Text);
}
