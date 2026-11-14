using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Jobs;
using Foundatio.Jobs.Store;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;
using Foundatio.Monitoring;
using Foundatio.Xunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Foundatio.Redis.Tests.Monitoring;

/// <summary>Instances share monitoring snapshots through a <see cref="RedisCacheClient"/>, as separate processes would.</summary>
public class RedisMonitoringTests(ITestOutputHelper output) : TestWithLoggingBase(output)
{
    private readonly InMemoryMessageTransport _transport = new();
    private readonly InMemoryJobStore _store = new();
    private readonly string _prefix = $"foundatio-tests:monitoring-{Guid.NewGuid():N}";
    private readonly Probe _probe = new();

    [Fact]
    public async Task GetOverviewAsync_WithARedisCache_ShowsEveryInstanceAndDropsStoppedOnes()
    {
        await using var worker = await StartAsync("importer", consumer: true);
        await using var dashboard = await StartAsync("dashboard", consumer: false);
        var monitor = dashboard.Services.GetRequiredService<IWorkMonitor>();

        await dashboard.Messages.SendAsync(new Import("spring"), new SendOptions { CorrelationId = "import-1" }, TestCancellationToken);
        await _probe.Reported.Task.WaitAsync(TimeSpan.FromSeconds(10), TestCancellationToken);
        await WaitForAsync(async () => (await monitor.GetActiveAsync(cancellationToken: TestCancellationToken)).Count == 1);

        var overview = await monitor.GetOverviewAsync(cancellationToken: TestCancellationToken);
        Assert.Equal(["dashboard", "importer"], overview.Instances.Select(i => i.ServiceName));
        var importer = overview.Instances[1];
        Assert.False(importer.IsCurrent);
        Assert.Equal([MessageSource.Queue("tests.redis-import")], importer.MessageSources);
        var active = Assert.Single(overview.Active);
        Assert.Equal((importer.InstanceId, "tests.redis-import", 40, "importing spring", "import-1"),
            (active.InstanceId, active.Name, active.Progress, active.ProgressMessage, active.CorrelationId));
        Assert.Equal(1, Assert.Single(overview.Backlog).InFlight);
        Assert.Equal(1, Assert.Single(overview.Stats).InFlight);

        _probe.Release.TrySetResult();
        await WaitForAsync(async () => (await monitor.GetStatsAsync(cancellationToken: TestCancellationToken)).Sum(s => s.Succeeded) == 1);
        Assert.Equal(WorkOutcome.Succeeded, Assert.Single(await monitor.GetRecentAsync(cancellationToken: TestCancellationToken)).Outcome);

        await worker.DisposeAsync();
        Assert.Equal(["dashboard"], (await monitor.GetInstancesAsync(TestCancellationToken)).Select(i => i.ServiceName));
    }

    [Fact]
    public async Task GetInstancesAsync_WhenAnInstanceStopsWithoutShuttingDown_DropsItOnceItsSnapshotExpires()
    {
        await using var dashboard = await StartAsync("dashboard", consumer: false);
        var crashed = await StartAsync("crashed", consumer: true);
        var monitor = dashboard.Services.GetRequiredService<IWorkMonitor>();
        await WaitForAsync(async () => (await monitor.GetInstancesAsync(TestCancellationToken)).Count == 2);

        await crashed.KillAsync();

        await WaitForAsync(async () => (await monitor.GetInstancesAsync(TestCancellationToken)).Count == 1);
        Assert.True(dashboard.Services.GetRequiredService<ICacheClient>() is RedisCacheClient);
    }

    public override async ValueTask DisposeAsync()
    {
        await _transport.DisposeAsync();
        if (SharedConnection.GetMuxer(Log) is { } muxer)
        {
            using var cache = new RedisCacheClient(o => o.ConnectionMultiplexer(muxer).LoggerFactory(Log));
            await cache.RemoveByPrefixAsync(_prefix);
        }

        await base.DisposeAsync();
    }

    private async Task<Instance> StartAsync(string serviceName, bool consumer)
    {
        var muxer = SharedConnection.GetMuxer(Log);
        Assert.SkipWhen(muxer is null, "RedisConnectionString is not configured.");

        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(Log);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton(_probe);
        services.AddSingleton<ICacheClient>(_ => new RedisCacheClient(o => o.ConnectionMultiplexer(muxer).LoggerFactory(Log)));
        services.AddFoundatioMessaging(m =>
        {
            m.UseTransport(_transport).UseServiceName(serviceName).Configure(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));
            m.MapMessage<Import>("tests.redis-import");
            if (consumer)
                m.AddConsumer<ImportHandler>();
        });
        services.AddFoundatioJobs(j => j.UseStore(_store).AddJob<NoopJob>().Configure(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5)));
        services.AddFoundatioMonitoring(m => m
            .UseCache()
            .Configure(o =>
            {
                o.KeyPrefix = _prefix;
                o.SnapshotInterval = TimeSpan.FromMilliseconds(500);
            }));

        var instance = new Instance(services.BuildServiceProvider());
        await instance.StartAsync();
        return instance;
    }

    private async Task WaitForAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The condition was not met in time.");

            await Task.Delay(100, TestCancellationToken);
        }
    }

    public record Import(string Season);

    public record Noop;

    public sealed class Probe
    {
        public TaskCompletionSource Reported { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class ImportHandler(Probe probe) : IMessageHandler<Import>
    {
        public async Task HandleAsync(Import message, IMessageContext context, CancellationToken cancellationToken)
        {
            await context.ReportProgressAsync(40, $"importing {message.Season}", cancellationToken);
            probe.Reported.TrySetResult();
            await probe.Release.Task.WaitAsync(cancellationToken);
        }
    }

    public sealed class NoopJob : IJobHandler<Noop>
    {
        public Task HandleAsync(Noop arguments, IJobContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class Instance(ServiceProvider services) : IAsyncDisposable
    {
        private readonly List<IHostedService> _started = [];
        private bool _disposed;

        public IServiceProvider Services => services;
        public IMessageClient Messages => services.GetRequiredService<IMessageClient>();

        public async Task StartAsync()
        {
            foreach (var service in services.GetServices<IHostedService>())
            {
                await service.StartAsync(CancellationToken.None);
                _started.Add(service);
            }
        }

        /// <summary>Disposes the services without stopping the host, leaving the snapshot in Redis as a crash would.</summary>
        public async Task KillAsync()
        {
            _disposed = true;
            await services.DisposeAsync();
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;

            _disposed = true;
            foreach (var service in Enumerable.Reverse(_started))
                await service.StopAsync(CancellationToken.None);

            await services.DisposeAsync();
        }
    }
}
