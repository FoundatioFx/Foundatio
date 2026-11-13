using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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
using Moq;
using Xunit;

namespace Foundatio.Tests.Monitoring;

public class WorkMonitorTests : TestWithLoggingBase
{
    private static readonly TimeSpan FastSnapshots = TimeSpan.FromMilliseconds(200);

    private readonly InMemoryMessageTransport _transport = new();
    private readonly InMemoryJobStore _store = new();
    private readonly InMemoryCacheClient _cache = new();
    private readonly Probe _probe = new();

    public WorkMonitorTests(ITestOutputHelper output) : base(output)
    {
    }

    [Fact]
    public async Task GetOverviewAsync_WithoutACache_ShowsOnlyThisInstanceLive()
    {
        await using var instance = await StartAsync("orders", useCache: false);
        var monitor = instance.Services.GetRequiredService<IWorkMonitor>();

        await instance.Messages.SendAsync(new Import("spring"), new SendOptions { CorrelationId = "import-1" }, TestCancellationToken);
        await _probe.Reported.Task.WaitAsync(TimeSpan.FromSeconds(10), TestCancellationToken);
        var overview = await monitor.GetOverviewAsync(cancellationToken: TestCancellationToken);

        var self = Assert.Single(overview.Instances);
        Assert.True(self.IsCurrent);
        Assert.Equal("orders", self.ServiceName);
        Assert.Equal(Environment.ProcessId, self.ProcessId);
        Assert.Equal([MessageSource.Queue("tests.import")], self.MessageSources);
        Assert.Equal(["tests.rebuild"], self.JobNames);

        var active = Assert.Single(overview.Active);
        Assert.Equal((WorkKind.Message, "tests.import", "queue:tests.import", self.InstanceId), (active.Kind, active.Name, active.Source, active.InstanceId));
        Assert.Equal((25, "importing spring", "import-1"), (active.Progress, active.ProgressMessage, active.CorrelationId));

        var backlog = Assert.Single(overview.Backlog);
        Assert.Equal((MessageSource.Queue("tests.import"), 0L, 1L), (backlog.Source, backlog.Queued, backlog.InFlight));
        var jobs = Assert.Single(overview.Jobs);
        Assert.Equal(("tests.rebuild", 0L, 0L), (jobs.Name, jobs.Queued, jobs.Running));
        Assert.Equal(1, Assert.Single(overview.Stats).InFlight);

        _probe.Release.TrySetResult();
        await WaitForAsync(async () => (await monitor.GetRecentAsync(cancellationToken: TestCancellationToken)).Count == 1);
        Assert.Empty(await monitor.GetActiveAsync(cancellationToken: TestCancellationToken));
    }

    [Fact]
    public async Task GetOverviewAsync_WithASharedCache_MergesTheOtherInstancesSnapshots()
    {
        await using var worker = await StartAsync("importer");
        await using var dashboard = await StartAsync("dashboard", consumer: false);
        var monitor = dashboard.Services.GetRequiredService<IWorkMonitor>();

        await dashboard.Messages.SendAsync(new Import("spring"), cancellationToken: TestCancellationToken);
        await _probe.Reported.Task.WaitAsync(TimeSpan.FromSeconds(10), TestCancellationToken);
        string busy = _probe.InstanceId!;
        Assert.Equal(worker.Services.GetRequiredService<MonitoringRuntime>().InstanceId, busy);

        await WaitForAsync(async () => (await monitor.GetActiveAsync(cancellationToken: TestCancellationToken)).Count == 1);
        var overview = await monitor.GetOverviewAsync(cancellationToken: TestCancellationToken);
        Assert.Equal(["dashboard", "importer"], overview.Instances.Select(i => i.ServiceName));
        Assert.Equal([true, false], overview.Instances.Select(i => i.IsCurrent));
        Assert.Equal(busy, Assert.Single(overview.Active).InstanceId);
        Assert.Equal(MessageSource.Queue("tests.import"), Assert.Single(overview.Backlog).Source);

        _probe.Release.TrySetResult();
        await WaitForAsync(async () => (await monitor.GetStatsAsync(cancellationToken: TestCancellationToken)).Sum(s => s.Succeeded) == 1);
        var recent = Assert.Single(await monitor.GetRecentAsync(cancellationToken: TestCancellationToken));
        Assert.Equal((busy, WorkOutcome.Succeeded, 25), (recent.InstanceId, recent.Outcome, recent.Progress));
        Assert.Empty(await monitor.GetActiveAsync(cancellationToken: TestCancellationToken));
    }

    [Fact]
    public async Task StopAsync_RemovesTheInstanceAtOnce_AndAKilledInstanceExpires()
    {
        await using var dashboard = await StartAsync("dashboard");
        var stopped = await StartAsync("stopped", configure: o => o.SnapshotInterval = TimeSpan.FromMinutes(1));
        var killed = await StartAsync("killed", configure: o => o.SnapshotInterval = TimeSpan.FromSeconds(1));
        var monitor = dashboard.Services.GetRequiredService<IWorkMonitor>();
        await WaitForAsync(async () => (await monitor.GetInstancesAsync(TestCancellationToken)).Count == 3);

        await stopped.DisposeAsync();
        Assert.DoesNotContain("stopped", (await monitor.GetInstancesAsync(TestCancellationToken)).Select(i => i.ServiceName));

        await killed.KillAsync();
        Assert.Contains("killed", (await monitor.GetInstancesAsync(TestCancellationToken)).Select(i => i.ServiceName));
        await WaitForAsync(async () => (await monitor.GetInstancesAsync(TestCancellationToken)).Count == 1);
    }

    [Fact]
    public async Task Publisher_WhenWorkReportsProgress_WritesSoonerThanTheInterval()
    {
        await using var worker = await StartAsync("importer", configure: o => o.SnapshotInterval = TimeSpan.FromMinutes(1));
        await using var dashboard = await StartAsync("dashboard", consumer: false, jobs: false);
        var monitor = dashboard.Services.GetRequiredService<IWorkMonitor>();
        await WaitForAsync(async () => (await monitor.GetInstancesAsync(TestCancellationToken)).Count == 1);

        await worker.Messages.SendAsync(new Import("spring"), cancellationToken: TestCancellationToken);
        await _probe.Reported.Task.WaitAsync(TimeSpan.FromSeconds(10), TestCancellationToken);

        await WaitForAsync(async () => (await monitor.GetActiveAsync(cancellationToken: TestCancellationToken)).Count == 1);
        _probe.Release.TrySetResult();
    }

    [Fact]
    public async Task GetStatsAsync_AcrossInstances_SumsCountsWeightsAveragesAndKeepsTheLongest()
    {
        await WriteSnapshotAsync("a", stats: [Stats("orders.placed", succeeded: 3, retried: 1, timed: 4, total: TimeSpan.FromMilliseconds(400), max: TimeSpan.FromMilliseconds(250), inFlight: 2)]);
        await WriteSnapshotAsync("b", stats: [Stats("orders.placed", succeeded: 1, timed: 1, total: TimeSpan.FromMilliseconds(600), max: TimeSpan.FromMilliseconds(600), inFlight: 1),
            Stats("orders.cancelled", skipped: 2)]);
        await using var dashboard = await StartAsync("dashboard", consumer: false, jobs: false);
        var monitor = dashboard.Services.GetRequiredService<IWorkMonitor>();

        var stats = await monitor.GetStatsAsync(cancellationToken: TestCancellationToken);

        Assert.Equal(["orders.cancelled", "orders.placed"], stats.Select(s => s.Name));
        var placed = stats[1];
        Assert.Equal((4L, 1L, 3), (placed.Succeeded, placed.Retried, placed.InFlight));
        Assert.Equal(TimeSpan.FromMilliseconds(200), placed.AverageDuration);
        Assert.Equal(TimeSpan.FromMilliseconds(600), placed.MaxDuration);
        Assert.Equal(TimeSpan.FromMinutes(5), placed.Window);
        Assert.Null(stats[0].AverageDuration);
        Assert.Equal(2, stats[0].Skipped);

        var onlyA = Assert.Single(await monitor.GetStatsAsync(new WorkFilter { InstanceId = "a" }, TestCancellationToken));
        Assert.Equal(3, onlyA.Succeeded);
        Assert.Equal("orders.cancelled", Assert.Single(await monitor.GetStatsAsync(new WorkFilter { Name = "orders.cancelled" }, TestCancellationToken)).Name);
        Assert.Empty(await monitor.GetStatsAsync(new WorkFilter { Kind = WorkKind.Job }, TestCancellationToken));
    }

    [Fact]
    public async Task GetInstancesAsync_SkipsSnapshotsThatAreUnreadableOrOfAnotherVersion()
    {
        await WriteSnapshotAsync("good");
        await _cache.SetAsync("foundatio:monitoring:instance:newer", "{\"v\":2,\"instanceId\":\"newer\",\"serviceName\":\"x\",\"machineName\":\"m\"}", TimeSpan.FromMinutes(1));
        await _cache.SetAsync("foundatio:monitoring:instance:broken", "{not json", TimeSpan.FromMinutes(1));
        await _cache.ListAddAsync("foundatio:monitoring:instances", ["newer", "broken", "missing"], TimeSpan.FromMinutes(1));
        await using var dashboard = await StartAsync("dashboard", consumer: false, jobs: false);

        var instances = await dashboard.Services.GetRequiredService<IWorkMonitor>().GetInstancesAsync(TestCancellationToken);

        Assert.Equal("good", Assert.Single(instances).InstanceId);
    }

    [Fact]
    public async Task GetActiveAndRecentAsync_WithAFilter_ReturnOnlyMatchingWork()
    {
        var now = DateTimeOffset.UtcNow;
        await WriteSnapshotAsync("a",
            active: [Active(WorkKind.Message, "orders.placed", now.AddSeconds(-5)), Active(WorkKind.Job, "reports.generate", now.AddSeconds(-9))],
            recent: [Recent(WorkKind.Job, "reports.generate", now.AddSeconds(-1)), Recent(WorkKind.Message, "orders.placed", now.AddSeconds(-2))]);
        await WriteSnapshotAsync("b", active: [Active(WorkKind.Message, "orders.placed", now.AddSeconds(-7))]);
        await using var dashboard = await StartAsync("dashboard", consumer: false, jobs: false);
        var monitor = dashboard.Services.GetRequiredService<IWorkMonitor>();

        var active = await monitor.GetActiveAsync(cancellationToken: TestCancellationToken);
        Assert.Equal([("a", "reports.generate"), ("b", "orders.placed"), ("a", "orders.placed")], active.Select(a => (a.InstanceId, a.Name)));
        Assert.Equal(["b"], (await monitor.GetActiveAsync(new WorkFilter { InstanceId = "b" }, TestCancellationToken)).Select(a => a.InstanceId));
        Assert.Equal(["reports.generate"], (await monitor.GetActiveAsync(new WorkFilter { Kind = WorkKind.Job }, TestCancellationToken)).Select(a => a.Name));

        var recent = await monitor.GetRecentAsync(cancellationToken: TestCancellationToken);
        Assert.Equal(["reports.generate", "orders.placed"], recent.Select(r => r.Name));
        Assert.Equal(["orders.placed"], (await monitor.GetRecentAsync(new WorkFilter { Name = "orders.placed" }, TestCancellationToken)).Select(r => r.Name));

        var overview = await monitor.GetOverviewAsync(new WorkFilter { InstanceId = "b" }, TestCancellationToken);
        Assert.Equal(["b"], overview.Instances.Select(i => i.InstanceId));
        Assert.Single(overview.Active);
        Assert.Empty(overview.Recent);
    }

    [Fact]
    public async Task GetBacklogAsync_WhenTheTransportReturnsNullOrThrows_ReturnsEmptyCountsAndCachesTheRead()
    {
        int calls = 0;
        var transport = new StatsTransport(_transport, source =>
        {
            Interlocked.Increment(ref calls);
            return source.Name switch
            {
                "tests.uncounted" => Task.FromResult<MessageSourceStats?>(null),
                "tests.broken" => throw new InvalidOperationException("the broker is down"),
                _ => _transport.GetStatsAsync(source)
            };
        });
        await WriteSnapshotAsync("a", sources: ["queue:tests.counted", "queue:tests.uncounted", "instance:tests.events/a", "deadletter:queue:x", "subscription:tests.events/billing"]);
        await WriteSnapshotAsync("b", sources: ["queue:tests.broken", "queue:tests.counted"]);
        await _transport.SendAsync(MessageDestination.Queue("tests.counted"), [new OutgoingMessage { MessageId = "m1", Body = new byte[1], Headers = new Dictionary<string, string>() }], TestCancellationToken);
        await using var dashboard = await StartAsync("dashboard", consumer: false, jobs: false, transport: transport, configure: o => o.SnapshotInterval = TimeSpan.FromMinutes(1));
        var monitor = dashboard.Services.GetRequiredService<IWorkMonitor>();

        var backlog = await monitor.GetBacklogAsync(TestCancellationToken);

        Assert.Equal(["queue:tests.broken", "queue:tests.counted", "queue:tests.uncounted", "subscription:tests.events/billing"], backlog.Select(b => b.Source.ToString()));
        Assert.Equal(1, backlog[1].Queued);
        Assert.All(new[] { backlog[0], backlog[2] }, b => Assert.True(b is { Queued: null, Delayed: null, InFlight: null, DeadLettered: null }, $"{b.Source} has counts."));
        Assert.Contains(Log.LogEntries, e => e.LogLevel == LogLevel.Warning && e.Message.Contains("queue:tests.broken"));

        await monitor.GetBacklogAsync(TestCancellationToken);
        await monitor.GetOverviewAsync(cancellationToken: TestCancellationToken);
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task GetJobCountsAsync_CountsEveryStateOfEveryJobTheInstancesRun()
    {
        await using var instance = await StartAsync("reports", useCache: false);
        var jobs = instance.Services.GetRequiredService<IJobClient>();
        _probe.Release.TrySetResult();
        var done = await jobs.EnqueueAsync(new Rebuild("orders"), cancellationToken: TestCancellationToken);
        await done.WaitForCompletionAsync(TimeSpan.FromSeconds(10), TestCancellationToken);
        await jobs.EnqueueAsync(new Rebuild("later"), new EnqueueOptions { Delay = TimeSpan.FromHours(1) }, TestCancellationToken);

        var counts = Assert.Single(await instance.Services.GetRequiredService<IWorkMonitor>().GetJobCountsAsync(TestCancellationToken));

        Assert.Equal(("tests.rebuild", 1L, 0L, 1L, 0L, 0L), (counts.Name, counts.Queued, counts.Running, counts.Succeeded, counts.Failed, counts.Cancelled));
    }

    [Fact]
    public async Task Publisher_WhenTheCacheFails_LogsAndKeepsHandlingMessages()
    {
        var cache = new Mock<ICacheClient>();
        cache.Setup(c => c.SetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan?>())).ThrowsAsync(new InvalidOperationException("the cache is down"));
        _probe.Release.TrySetResult();
        await using var instance = await StartAsync("orders", cache: cache.Object);

        await instance.Messages.SendAsync(new Import("spring"), cancellationToken: TestCancellationToken);

        await WaitForAsync(() => Task.FromResult(_probe.Handled == 1));
        await WaitForAsync(() => Task.FromResult(Log.LogEntries.Any(e => e.LogLevel == LogLevel.Warning && e.Message.Contains("Could not write the monitoring snapshot"))));
    }

    [Fact]
    public async Task AddFoundatioMonitoring_WithUseCacheButNoCacheRegistered_FailsTheHostStart()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(Log);
        services.AddFoundatioJobs(j => j.UseInMemory().AddJob<RebuildJob>());
        services.AddFoundatioMonitoring(m => m.UseCache());
        await using var provider = services.BuildServiceProvider();

        var publisher = provider.GetServices<IHostedService>().OfType<SnapshotPublisher>().Single();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => publisher.StartAsync(TestCancellationToken));
        Assert.Contains("ICacheClient", exception.Message);
    }

    [Theory]
    [InlineData("interval")]
    [InlineData("expiry")]
    [InlineData("window")]
    [InlineData("prefix")]
    public void AddFoundatioMonitoring_WithInvalidOptions_ThrowsWhenResolved(string invalid)
    {
        var services = new ServiceCollection();
        services.AddFoundatioMonitoring(m => m.Configure(o =>
        {
            switch (invalid)
            {
                case "interval": o.SnapshotInterval = TimeSpan.Zero; break;
                case "expiry": o.SnapshotExpiry = o.SnapshotInterval; break;
                case "window": o.StatsWindow = TimeSpan.FromSeconds(30); break;
                case "prefix": o.KeyPrefix = " "; break;
            }
        }));
        using var provider = services.BuildServiceProvider();

        Assert.ThrowsAny<ArgumentException>(() => provider.GetRequiredService<IWorkMonitor>());
    }

    public override async ValueTask DisposeAsync()
    {
        await _transport.DisposeAsync();
        _cache.Dispose();
        await base.DisposeAsync();
    }

    private async Task<Instance> StartAsync(string serviceName, bool useCache = true, bool consumer = true, bool jobs = true, ICacheClient? cache = null,
        IMessageTransport? transport = null, Action<MonitoringOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(Log);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton(_probe);
        services.AddFoundatioMessaging(m =>
        {
            m.UseTransport(transport ?? _transport).UseServiceName(serviceName).Configure(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));
            m.MapMessage<Import>("tests.import");
            if (consumer)
                m.AddConsumer<ImportHandler>();
        });
        services.AddFoundatioJobs(j =>
        {
            j.UseStore(_store).Configure(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));
            if (jobs)
                j.AddJob<RebuildJob>();
        });
        services.AddFoundatioMonitoring(m =>
        {
            m.Configure(o =>
            {
                o.ServiceName = serviceName;
                o.SnapshotInterval = FastSnapshots;
                configure?.Invoke(o);
            });
            if (useCache)
                m.UseCache(cache ?? _cache);
        });

        var instance = new Instance(services.BuildServiceProvider());
        await instance.StartAsync();
        return instance;
    }

    private async Task WriteSnapshotAsync(string instanceId, IReadOnlyList<string>? sources = null, IReadOnlyList<WorkStatsEntry>? stats = null,
        IReadOnlyList<ActiveWorkEntry>? active = null, IReadOnlyList<CompletedWorkEntry>? recent = null)
    {
        var snapshot = new InstanceSnapshot
        {
            InstanceId = instanceId,
            ServiceName = $"service-{instanceId}",
            MachineName = "test-machine",
            ProcessId = 42,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            UpdatedAt = DateTimeOffset.UtcNow,
            MessageSources = sources ?? [],
            Active = active ?? [],
            Recent = recent ?? [],
            Stats = stats ?? [],
            StatsWindow = TimeSpan.FromMinutes(5)
        };

        await _cache.SetAsync($"foundatio:monitoring:instance:{instanceId}", JsonSerializer.Serialize(snapshot, MonitoringJsonContext.Default.InstanceSnapshot), TimeSpan.FromMinutes(1));
        await _cache.ListAddAsync("foundatio:monitoring:instances", [instanceId], TimeSpan.FromMinutes(1));
    }

    private static WorkStatsEntry Stats(string name, long succeeded = 0, long retried = 0, long skipped = 0, long timed = 0, TimeSpan total = default, TimeSpan max = default, int inFlight = 0) => new()
    {
        Kind = WorkKind.Message,
        Name = name,
        Source = "queue:orders",
        Succeeded = succeeded,
        Retried = retried,
        Skipped = skipped,
        Timed = timed,
        TotalDuration = total,
        MaxDuration = max,
        InFlight = inFlight
    };

    private static ActiveWorkEntry Active(WorkKind kind, string name, DateTimeOffset startedAt) => new() { Kind = kind, Id = Guid.NewGuid().ToString("N"), Name = name, StartedAt = startedAt };

    private static CompletedWorkEntry Recent(WorkKind kind, string name, DateTimeOffset completedAt) => new()
    {
        Kind = kind,
        Id = Guid.NewGuid().ToString("N"),
        Name = name,
        StartedAt = completedAt,
        CompletedAt = completedAt,
        Outcome = WorkOutcome.Failed
    };

    private async Task WaitForAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The condition was not met in time.");

            await Task.Delay(50, TestCancellationToken);
        }
    }

    [Message("tests.import")]
    public record Import(string Season);

    [Job(Name = "tests.rebuild")]
    public record Rebuild(string Index);

    public sealed class Probe
    {
        private int _handled;

        public string? InstanceId { get; set; }
        public int Handled => Volatile.Read(ref _handled);
        public TaskCompletionSource Reported { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void MarkHandled() => Interlocked.Increment(ref _handled);
    }

    public sealed class ImportHandler(Probe probe, IServiceProvider services) : IMessageHandler<Import>
    {
        public async Task HandleAsync(Import message, IMessageContext context, CancellationToken cancellationToken)
        {
            await context.ReportProgressAsync(25, $"importing {message.Season}", cancellationToken);
            probe.InstanceId = services.GetRequiredService<MonitoringRuntime>().InstanceId;
            probe.Reported.TrySetResult();
            await probe.Release.Task.WaitAsync(cancellationToken);
            probe.MarkHandled();
        }
    }

    public sealed class RebuildJob(Probe probe) : IJobHandler<Rebuild>
    {
        public Task HandleAsync(Rebuild arguments, IJobContext context, CancellationToken cancellationToken) => probe.Release.Task.WaitAsync(cancellationToken);
    }

    private sealed class StatsTransport(IMessageTransport inner, Func<MessageSource, Task<MessageSourceStats?>> getStats) : IMessageTransport
    {
        public TransportCapabilities Capabilities => inner.Capabilities;

        public Task SendAsync(MessageDestination destination, IReadOnlyList<OutgoingMessage> messages, CancellationToken cancellationToken = default) => inner.SendAsync(destination, messages, cancellationToken);

        public Task<IMessageReceiver> CreateReceiverAsync(MessageSource source, ReceiverOptions options, CancellationToken cancellationToken = default) => inner.CreateReceiverAsync(source, options, cancellationToken);

        public Task DeleteAsync(MessageSource source, CancellationToken cancellationToken = default) => inner.DeleteAsync(source, cancellationToken);

        public Task<MessageSourceStats?> GetStatsAsync(MessageSource source, CancellationToken cancellationToken = default) => getStats(source);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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

        /// <summary>Disposes the services without stopping the host, as a crash would leave the cache.</summary>
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
