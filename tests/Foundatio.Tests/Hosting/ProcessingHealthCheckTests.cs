using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Messaging;
using Foundatio.Utility;
using Foundatio.Xunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Foundatio.Tests.Hosting;

public class ProcessingHealthCheckTests : TestWithLoggingBase
{
    private const string Messaging = "foundatio-messaging";
    private const string Jobs = "foundatio-jobs";
    private const string PingQueue = "queue:tests.ping";
    private static readonly TimeSpan UnhealthyAfter = TimeSpan.FromMinutes(2);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly TestStartupGate _gate = new();
    private readonly FlakyMessageTransport _transport;
    private readonly FlakyJobStore _store;
    private readonly WorkGate _work = new();
    private ServiceProvider? _provider;
    private List<IHostedService> _hostedServices = [];
    private bool _stopHostedServices = true;

    public ProcessingHealthCheckTests(ITestOutputHelper output) : base(output)
    {
        _transport = new FlakyMessageTransport(_time);
        _store = new FlakyJobStore(_time);
    }

    [Fact]
    public async Task AddFoundatioMessaging_BeforeTheStartupGateOpens_ReportsHealthyStarting()
    {
        await StartAsync(messaging: m => m.AddConsumer<Ping>(Handle));

        var entry = await CheckAsync(Messaging);

        Assert.Equal(HealthStatus.Healthy, entry.Status);
        Assert.Equal($"Starting: {PingQueue}", entry.Description);
        Assert.Equal("Starting", Detail(entry, PingQueue, "state"));
    }

    [Fact]
    public async Task AddFoundatioMessaging_WhenReceiving_ReportsHealthy()
    {
        await StartAsync(messaging: m => m.AddConsumer<Ping>(Handle));

        _gate.Open(true);
        var entry = await WaitForAsync(Messaging, e => Detail(e, PingQueue, "state") is "Running");

        Assert.Equal(HealthStatus.Healthy, entry.Status);
        Assert.Equal($"Running: {PingQueue}", entry.Description);
        Assert.Equal(0, Detail(entry, PingQueue, "active"));
    }

    [Fact]
    public async Task AddFoundatioMessaging_WhenReceiveErrorsAreRetried_ReportsDegradedWithTheLastError()
    {
        _transport.FailReceive = true;
        await StartAsync(messaging: m => m.AddConsumer<Ping>(Handle));

        _gate.Open(true);
        var entry = await WaitForAsync(Messaging, e => e.Status != HealthStatus.Healthy);

        Assert.Equal(HealthStatus.Degraded, entry.Status);
        Assert.Equal($"{PingQueue}: retrying after errors for 00:00:00 (InvalidOperationException: The connection was reset)", entry.Description);
        Assert.Equal("Running", Detail(entry, PingQueue, "state"));
        Assert.Equal(_time.GetUtcNow(), Detail(entry, PingQueue, "failingSince"));
    }

    [Fact]
    public async Task AddFoundatioMessaging_WhenReceivingFailsLongerThanTheThreshold_ReportsUnhealthy()
    {
        _transport.FailReceive = true;
        await StartAsync(messaging: m => m.AddConsumer<Ping>(Handle));
        _gate.Open(true);
        await WaitForAsync(Messaging, e => e.Status == HealthStatus.Degraded);

        _time.Advance(UnhealthyAfter - TimeSpan.FromSeconds(1));
        Assert.Equal(HealthStatus.Degraded, (await CheckAsync(Messaging)).Status);

        _time.Advance(TimeSpan.FromSeconds(1));
        var entry = await CheckAsync(Messaging);

        Assert.Equal(HealthStatus.Unhealthy, entry.Status);
        Assert.Equal($"{PingQueue}: failing for 00:02:00 (InvalidOperationException: The connection was reset)", entry.Description);
    }

    [Fact]
    public async Task AddFoundatioMessaging_WhenReceivingRecovers_ReportsHealthyAgain()
    {
        _transport.FailReceive = true;
        await StartAsync(messaging: m => m.AddConsumer<Ping>(Handle));
        _gate.Open(true);
        await WaitForAsync(Messaging, e => e.Status == HealthStatus.Degraded);

        _transport.FailReceive = false;
        await _provider!.GetRequiredService<IMessageClient>().SendAsync(new Ping(), cancellationToken: TestCancellationToken);
        var entry = await WaitForAsync(Messaging, e => e.Status == HealthStatus.Healthy, advanceTime: true);

        Assert.Equal($"Running: {PingQueue}", entry.Description);
        Assert.Equal("InvalidOperationException: The connection was reset", Detail(entry, PingQueue, "lastError"));
        Assert.False(((Dictionary<string, object>)entry.Data[PingQueue]).ContainsKey("failingSince"));
    }

    [Fact]
    public async Task AddFoundatioMessaging_WhenConnectingFailsAtStartup_ReportsDegradedUntilItConnects()
    {
        _transport.FailConnect = true;
        await StartAsync(messaging: m => m.AddConsumer<Ping>(Handle));

        _gate.Open(true);
        var failing = await WaitForAsync(Messaging, e => e.Status != HealthStatus.Healthy);
        _transport.FailConnect = false;
        var connected = await WaitForAsync(Messaging, e => e.Status == HealthStatus.Healthy, advanceTime: true);

        Assert.Equal(HealthStatus.Degraded, failing.Status);
        Assert.Equal("Starting", Detail(failing, PingQueue, "state"));
        Assert.Contains("The broker is unreachable", failing.Description);
        Assert.Equal("Running", Detail(connected, PingQueue, "state"));
    }

    [Fact]
    public async Task AddFoundatioMessaging_WhenTheReceiverFaults_ReplacesItAndRecovers()
    {
        _transport.FaultReceive = true;
        await StartAsync(messaging: m => m.AddConsumer<Ping>(Handle));
        _gate.Open(true);
        var faulted = await WaitForAsync(Messaging, e => e.Status == HealthStatus.Degraded);

        _transport.FaultReceive = false;
        await WaitForAsync(Messaging, e => e.Status == HealthStatus.Healthy, advanceTime: true);

        Assert.Contains("MessageReceiverFaultedException: The consumer hit a fatal error", faulted.Description);
        Assert.Equal(2, _transport.ReceiversCreated);
    }

    [Fact]
    public async Task AddFoundatioMessaging_WhenReceivingKeepsFailing_ReplacesTheReceiverAfterFiveFailures()
    {
        _transport.FailReceive = true;
        await StartAsync(messaging: m => m.AddConsumer<Ping>(Handle));
        _gate.Open(true);
        await WaitForAsync(Messaging, e => e.Status == HealthStatus.Degraded);
        Assert.Equal(1, _transport.ReceiversCreated);

        var entry = await WaitForAsync(Messaging, _ => _transport.ReceiversCreated == 2, advanceTime: true);

        Assert.Equal(HealthStatus.Degraded, entry.Status);
    }

    [Fact]
    public async Task AddFoundatioMessaging_WhenAProcessorStopsUnexpectedly_ReportsUnhealthy()
    {
        _transport.ReturnNull = true;
        await StartAsync(messaging: m => m.AddConsumer<Ping>(Handle));

        _gate.Open(true);
        var entry = await WaitForAsync(Messaging, e => e.Status != HealthStatus.Healthy);

        Assert.Equal(HealthStatus.Unhealthy, entry.Status);
        Assert.StartsWith($"{PingQueue}: stopped (NullReferenceException", entry.Description);
        Assert.Equal("Faulted", Detail(entry, PingQueue, "state"));
    }

    [Fact]
    public async Task AddFoundatioMessagingAndJobs_WhenStartupFails_ReportUnhealthy()
    {
        await StartAsync(messaging: m => m.AddConsumer<Ping>(Handle), jobs: j => j.AddJob<WorkJob>());

        _gate.Open(false);
        var messaging = await WaitForAsync(Messaging, e => e.Status != HealthStatus.Healthy);
        var jobs = await WaitForAsync(Jobs, e => e.Status != HealthStatus.Healthy);

        Assert.Equal(HealthStatus.Unhealthy, messaging.Status);
        Assert.Equal($"{PingQueue}: stopped (Startup failed; message handlers will not run)", messaging.Description);
        Assert.Equal(HealthStatus.Unhealthy, jobs.Status);
        Assert.Equal("worker: stopped (Startup failed; jobs will not run)", jobs.Description);
    }

    [Fact]
    public async Task AddFoundatioMessaging_WithAFailureStatusAndTags_ReportsTheFailureStatusInsteadOfUnhealthy()
    {
        _transport.ReturnNull = true;
        await StartAsync(messaging: m => m.AddConsumer<Ping>(Handle),
            health: h => h.AddFoundatioMessaging("messaging-live", failureStatus: HealthStatus.Degraded, tags: ["live"]));

        _gate.Open(true);
        var entry = await WaitForAsync("messaging-live", e => Detail(e, PingQueue, "state") is "Faulted");

        Assert.Equal(HealthStatus.Degraded, entry.Status);
        Assert.Equal(["live"], entry.Tags);
    }

    [Fact]
    public async Task AddFoundatioMessaging_WhenNothingIsHandled_SaysSo()
    {
        await StartAsync(messaging: _ => { }, health: h => h.AddFoundatioMessaging());

        var sendOnly = await CheckAsync(Messaging);

        Assert.Equal(HealthStatus.Healthy, sendOnly.Status);
        Assert.Equal("No message handlers are registered", sendOnly.Description);
    }

    [Fact]
    public async Task AddFoundatioJobsHealthCheck_WithoutAddFoundatioJobs_FailsAtStartup()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(messaging: _ => { }, health: h => h.AddFoundatioMessaging().AddFoundatioJobs()));

        Assert.Contains("AddFoundatioJobs", error.Message);
        Assert.DoesNotContain("AddFoundatioMessaging", error.Message);
    }

    [Fact]
    public async Task AddFoundatioMessagingHealthCheck_WithoutAddFoundatioMessaging_FailsHostStartup()
    {
        using var host = new HostBuilder().ConfigureServices(s => s.AddHealthChecks().AddFoundatioMessaging()).Build();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(TestCancellationToken));

        Assert.Contains("AddFoundatioMessaging", error.Message);
    }

    [Fact]
    public async Task AddFoundatioJobs_BeforeTheStartupGateOpens_ReportsStartingWithEveryJobType()
    {
        await StartAsync(jobs: j => j.AddJob<WorkJob>().AddCronJob<TickJob>("0 * * * *"));

        var entry = await CheckAsync(Jobs);

        Assert.Equal(HealthStatus.Healthy, entry.Status);
        Assert.Equal("Starting: worker, scheduler", entry.Description);
        var running = (IReadOnlyDictionary<string, int>)Detail(entry, "worker", "activeByName");
        Assert.Equal(["tests.tick", "tests.work"], running.Keys.Order());
        Assert.All(running.Values, count => Assert.Equal(0, count));
        Assert.Equal("Starting", Detail(entry, "scheduler", "state"));
    }

    [Fact]
    public async Task AddFoundatioJobs_WhenJobsRun_ReportsRunningJobsPerJobType()
    {
        await StartAsync(jobs: j => j.AddJob<WorkJob>());
        _gate.Open(true);

        await _provider!.GetRequiredService<IJobClient>().EnqueueAsync(new Work(), cancellationToken: TestCancellationToken);
        var entry = await WaitForAsync(Jobs, e => Detail(e, "worker", "active") is 1);
        _work.Release();

        Assert.Equal(HealthStatus.Healthy, entry.Status);
        Assert.Equal(1, ((IReadOnlyDictionary<string, int>)Detail(entry, "worker", "activeByName"))["tests.work"]);
    }

    [Fact]
    public async Task AddFoundatioJobs_WhenClaimErrorsContinue_ReportsDegradedThenUnhealthy()
    {
        _store.FailClaim = true;
        await StartAsync(jobs: j => j.AddJob<WorkJob>());

        _gate.Open(true);
        var degraded = await WaitForAsync(Jobs, e => e.Status != HealthStatus.Healthy);
        _time.Advance(UnhealthyAfter);
        var unhealthy = await CheckAsync(Jobs);

        Assert.Equal(HealthStatus.Degraded, degraded.Status);
        Assert.Equal("worker: retrying after errors for 00:00:00 (InvalidOperationException: The job store timed out)", degraded.Description);
        Assert.Equal(HealthStatus.Unhealthy, unhealthy.Status);
        Assert.StartsWith("worker: failing for 00:02:", unhealthy.Description);
    }

    [Fact]
    public async Task AddFoundatioJobs_WhenClaimingRecovers_ReportsHealthyAgain()
    {
        _store.FailClaim = true;
        await StartAsync(jobs: j => j.AddJob<WorkJob>());
        _gate.Open(true);
        await WaitForAsync(Jobs, e => e.Status == HealthStatus.Degraded);

        _store.FailClaim = false;
        _work.Release();
        await _provider!.GetRequiredService<IJobClient>().EnqueueAsync(new Work(), cancellationToken: TestCancellationToken);
        var entry = await WaitForAsync(Jobs, e => e.Status == HealthStatus.Healthy);

        Assert.Equal("Running: worker", entry.Description);
    }

    [Fact]
    public async Task AddFoundatioJobs_WhenTheWorkerStopsUnexpectedly_ReportsUnhealthy()
    {
        _store.ReturnCorruptJob = true;
        _stopHostedServices = false;
        await StartAsync(jobs: j => j.AddJob<WorkJob>());

        _gate.Open(true);
        var entry = await WaitForAsync(Jobs, e => e.Status != HealthStatus.Healthy);

        Assert.Equal(HealthStatus.Unhealthy, entry.Status);
        Assert.StartsWith("worker: stopped (ArgumentNullException", entry.Description);
    }

    [Fact]
    public async Task AddFoundatioJobs_WhenSchedulingFails_ReportsTheSchedulerDegraded()
    {
        _store.FailCreate = true;
        await StartAsync(jobs: j => j.AddCronJob<TickJob>("* * * * * *"));

        _gate.Open(true);
        var entry = await WaitForAsync(Jobs, e => e.Status != HealthStatus.Healthy);

        Assert.Equal(HealthStatus.Degraded, entry.Status);
        Assert.Equal("scheduler: retrying after errors for 00:00:00 (Schedule tests.tick: InvalidOperationException: The job store is read-only)", entry.Description);
        Assert.Equal("Running", Detail(entry, "worker", "state"));
    }

    public override async ValueTask DisposeAsync()
    {
        _work.Release();
        if (_stopHostedServices)
        {
            foreach (var service in Enumerable.Reverse(_hostedServices))
                await service.StopAsync(CancellationToken.None);
        }

        if (_provider is not null)
            await _provider.DisposeAsync();

        await _transport.DisposeAsync();
        await base.DisposeAsync();
    }

    private async Task StartAsync(Action<FoundatioMessagingBuilder>? messaging = null, Action<FoundatioJobsBuilder>? jobs = null, Action<IHealthChecksBuilder>? health = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(Log);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton<TimeProvider>(_time);
        services.AddSingleton<IStartupGate>(_gate);
        services.AddSingleton(_work);

        if (messaging is not null)
        {
            services.AddFoundatioMessaging(m =>
            {
                m.UseTransport(_transport).UseServiceName("tests").Configure(o => o.ShutdownTimeout = TimeSpan.Zero);
                messaging(m);
            });
        }

        if (jobs is not null)
        {
            services.AddFoundatioJobs(j =>
            {
                j.UseStore(_store).Configure(o => o.ShutdownTimeout = TimeSpan.Zero);
                jobs(j);
            });
        }

        var healthChecks = services.AddHealthChecks();
        if (health is not null)
        {
            health(healthChecks);
        }
        else
        {
            if (messaging is not null)
                healthChecks.AddFoundatioMessaging(unhealthyAfter: UnhealthyAfter);
            if (jobs is not null)
                healthChecks.AddFoundatioJobs(unhealthyAfter: UnhealthyAfter);
        }

        _provider = services.BuildServiceProvider();
        _hostedServices = _provider.GetServices<IHostedService>().ToList();
        foreach (var service in _hostedServices)
            await service.StartAsync(TestCancellationToken);
    }

    private async Task<HealthReportEntry> CheckAsync(string name)
    {
        var report = await _provider!.GetRequiredService<HealthCheckService>().CheckHealthAsync(r => r.Name == name, TestCancellationToken);
        return report.Entries[name];
    }

    /// <summary>Polls the check until <paramref name="condition"/> holds; optionally advancing time to release backoff delays.</summary>
    private async Task<HealthReportEntry> WaitForAsync(string name, Func<HealthReportEntry, bool> condition, bool advanceTime = false)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var entry = await CheckAsync(name);
            if (condition(entry))
                return entry;

            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"{name} is still {entry.Status}: {entry.Description}");

            if (advanceTime)
                _time.Advance(TimeSpan.FromSeconds(1));

            await Task.Delay(10, TestCancellationToken);
        }
    }

    private static object Detail(HealthReportEntry entry, string component, string key)
    {
        return ((Dictionary<string, object>)entry.Data[component])[key];
    }

    private static Task Handle(Ping message, IMessageContext context, CancellationToken cancellationToken) => Task.CompletedTask;

    [Message("tests.ping")]
    public record Ping;

    [Job(Name = "tests.work")]
    public record Work;

    public sealed class WorkGate
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Released => _released.Task;

        public void Release() => _released.TrySetResult();
    }

    public sealed class WorkJob(WorkGate gate) : IDurableJob<Work>
    {
        public Task RunAsync(Work arguments, IJobContext context, CancellationToken cancellationToken) => gate.Released.WaitAsync(cancellationToken);
    }

    [Job(Name = "tests.tick")]
    public sealed class TickJob : IJob
    {
        public Task<JobResult> RunAsync(CancellationToken cancellationToken = default) => Task.FromResult(JobResult.Success);
    }

    private sealed class TestStartupGate : IStartupGate
    {
        private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Open(bool success) => _result.TrySetResult(success);

        public Task<bool> WaitAsync(CancellationToken cancellationToken) => _result.Task.WaitAsync(cancellationToken);
    }
}
