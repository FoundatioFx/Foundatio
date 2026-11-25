using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Jobs.Store;
using Foundatio.Xunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Foundatio.Tests.Hosting;

public class CronJobHealthCheckTests : TestWithLoggingBase
{
    private const string Schedule = "tests.count-events";
    private const string Check = "count-events";

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemoryJobStore _store;
    private readonly List<ServiceProvider> _providers = [];
    private readonly List<IHostedService> _hostedServices = [];

    public CronJobHealthCheckTests(ITestOutputHelper output) : base(output)
    {
        _store = new InMemoryJobStore(_time);
    }

    [Fact]
    public async Task AddFoundatioCronJob_WhenAnotherInstanceRanTheLatestOccurrence_ReportsHealthy()
    {
        var runner = await StartAsync(runsSchedule: true);
        var other = await StartAsync(runsSchedule: false);
        await WaitForSucceededAsync(1);

        var entry = await CheckAsync(other);

        Assert.Equal(HealthStatus.Healthy, entry.Status);
        Assert.Contains("2026-01-01T12:00:00", entry.Description);
        Assert.Equal(await CheckAsync(runner), entry, (a, b) => a.Status == b.Status && a.Description == b.Description);
    }

    [Fact]
    public async Task AddFoundatioCronJob_WithZeroRetention_ReportsHealthyUntilTheWindowPassesWithoutASuccess()
    {
        var runner = await StartAsync(runsSchedule: true);
        await WaitForSucceededAsync(1);
        for (int occurrence = 2; occurrence <= 4; occurrence++)
        {
            _time.Advance(TimeSpan.FromSeconds(5));
            await WaitForSucceededAsync(occurrence);
            Assert.Equal(HealthStatus.Healthy, (await CheckAsync(runner)).Status);
        }

        await StopAsync();
        var other = await StartAsync(runsSchedule: false);
        _time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(HealthStatus.Healthy, (await CheckAsync(other)).Status);

        _time.Advance(TimeSpan.FromSeconds(10));
        var entry = await CheckAsync(other);

        Assert.Equal(HealthStatus.Unhealthy, entry.Status);
        Assert.Equal($"No occurrence of CRON schedule {Schedule} created in the last 00:00:15 has succeeded", entry.Description);
    }

    [Fact]
    public async Task AddFoundatioCronJob_WhenTheOccurrencesFail_ReportsTheFailureStatus()
    {
        var other = await StartAsync(runsSchedule: false, failureStatus: HealthStatus.Degraded);
        await _store.CreateAsync(new JobInfo { Id = "cron:tests.count-events:20260101T120000Z", Name = Schedule, ScheduleName = Schedule, CreatedAt = _time.GetUtcNow(), AvailableAt = _time.GetUtcNow() }, TestCancellationToken);
        var claimed = await _store.ClaimAsync(new JobClaimRequest { Names = [Schedule], WorkerId = "w", LeaseDuration = TimeSpan.FromMinutes(1) }, TestCancellationToken);
        await _store.CompleteAsync(claimed!.Id, claimed.ClaimToken!, JobCompletion.Failed(TimeSpan.FromDays(1), "Elasticsearch is down"), TestCancellationToken);

        var entry = await CheckAsync(other);

        Assert.Equal(HealthStatus.Degraded, entry.Status);
        Assert.Contains("has succeeded", entry.Description);
    }

    [Theory]
    [InlineData("0 0 * * * *", 65 * 60)]
    [InlineData("0 0 1 * * *", 25 * 60 * 60)]
    public async Task AddFoundatioCronJob_OnAFreshStore_ReportsHealthyUntilTheScheduleIsOverdue(string cron, int windowSeconds)
    {
        var window = TimeSpan.FromSeconds(windowSeconds);
        var host = await StartAsync(runsSchedule: false, cron: cron, succeededWithin: window);

        var entry = await CheckAsync(host);
        Assert.Equal(HealthStatus.Healthy, entry.Status);
        Assert.Equal($"CRON schedule {Schedule} is new: no occurrence has had {FormatWindow(window)} to succeed yet", entry.Description);

        _time.Advance(window - TimeSpan.FromSeconds(1));
        Assert.Equal(HealthStatus.Healthy, (await CheckAsync(host)).Status);

        _time.Advance(TimeSpan.FromSeconds(1));
        entry = await CheckAsync(host);
        Assert.Equal(HealthStatus.Unhealthy, entry.Status);
        Assert.Equal($"No occurrence of CRON schedule {Schedule} created in the last {FormatWindow(window)} has succeeded", entry.Description);
    }

    [Fact]
    public async Task AddFoundatioCronJob_OnAFreshStore_ReportsTheFirstSuccessThenFailsOnceOccurrencesStop()
    {
        _time.Advance(TimeSpan.FromMinutes(30));
        var options = new CronJobOptions { MisfireThreshold = TimeSpan.FromMinutes(1) };
        var runner = await StartAsync(runsSchedule: true, cron: "0 0 * * * *", succeededWithin: TimeSpan.FromMinutes(65), cronOptions: options);
        Assert.Equal(HealthStatus.Healthy, (await CheckAsync(runner)).Status);

        _time.Advance(TimeSpan.FromMinutes(30));
        await WaitForSucceededAsync(1);
        var entry = await CheckAsync(runner);
        Assert.Equal(HealthStatus.Healthy, entry.Status);
        Assert.Contains("last succeeded with the occurrence scheduled for 2026-01-01T13:00:00", entry.Description);

        await StopAsync();
        var other = await StartAsync(runsSchedule: false, cron: "0 0 * * * *", succeededWithin: TimeSpan.FromMinutes(65), cronOptions: options);
        _time.Advance(TimeSpan.FromMinutes(65));
        Assert.Equal(HealthStatus.Healthy, (await CheckAsync(other)).Status);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(HealthStatus.Unhealthy, (await CheckAsync(other)).Status);
    }

    [Fact]
    public async Task AddFoundatioCronJob_OnAnInstanceStartedAfterAnOccurrenceBecameOverdue_ReportsTheFailureLikeTheOthers()
    {
        var window = TimeSpan.FromMinutes(65);
        var earlier = await StartAsync(runsSchedule: false, cron: "0 0 * * * *", succeededWithin: window);
        Assert.Equal(HealthStatus.Healthy, (await CheckAsync(earlier)).Status);
        await _store.CreateAsync(new JobInfo { Id = "cron:tests.count-events:20260101T120000Z", Name = Schedule, ScheduleName = Schedule, CreatedAt = _time.GetUtcNow(), AvailableAt = _time.GetUtcNow() }, TestCancellationToken);

        _time.Advance(window + TimeSpan.FromSeconds(1));
        var later = await StartAsync(runsSchedule: false, cron: "0 0 * * * *", succeededWithin: window);

        Assert.Equal(HealthStatus.Unhealthy, (await CheckAsync(earlier)).Status);
        Assert.Equal(HealthStatus.Unhealthy, (await CheckAsync(later)).Status);
    }

    [Fact]
    public async Task AddFoundatioCronJob_ForAScheduleThisProcessDoesNotRegister_ReportsUnhealthyNamingTheSchedules()
    {
        var host = await StartAsync(runsSchedule: false, scheduleName: "tests.nope");

        var entry = await CheckAsync(host);

        Assert.Equal(HealthStatus.Unhealthy, entry.Status);
        Assert.Contains("tests.nope", entry.Description);
        Assert.Contains(Schedule, entry.Description);
    }

    [Fact]
    public async Task AddFoundatioCronJob_OfAJobType_ChecksTheScheduleItsAddCronJobRegistered()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(Log);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddFoundatioJobs(j => j
            .UseStore(_store)
            .Configure(o => { o.TimeProvider = _time; o.ShutdownTimeout = TimeSpan.Zero; })
            .AddCronJob<CountEventsJob>("*/5 * * * * *")
            .AddCronJob<LegacyJob<LegacyCountJob>>("*/5 * * * * *"));
        services.AddHealthChecks()
            .AddFoundatioCronJob<CountEventsJob>(TimeSpan.FromSeconds(15))
            .AddFoundatioCronJob<LegacyCountJob>(TimeSpan.FromSeconds(15))
            .AddFoundatioCronJob<LegacyJob<LegacyCountJob>>(TimeSpan.FromSeconds(15), name: "legacy-wrapped")
            .AddFoundatioCronJob<UnscheduledJob>(TimeSpan.FromSeconds(15));
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        foreach (var service in provider.GetServices<IHostedService>())
        {
            await service.StartAsync(TestCancellationToken);
            _hostedServices.Add(service);
        }

        string legacyName = typeof(LegacyCountJob).FullName!.Replace('+', '.');
        Assert.Equal(legacyName, JobNames.Of<LegacyCountJob>());
        Assert.Equal(legacyName, JobNames.Of<LegacyJob<LegacyCountJob>>());
        Assert.Equal(Schedule, JobNames.Of<CountEventsJob>());
        await WaitForSucceededAsync(1);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (await _store.CountAsync(JobState.Succeeded, legacyName, TestCancellationToken) < 1)
        {
            Assert.True(DateTime.UtcNow < deadline, "The legacy job did not succeed in time.");
            await Task.Delay(10, TestCancellationToken);
        }

        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(TestCancellationToken);

        Assert.Equal(HealthStatus.Healthy, report.Entries[$"foundatio-cron-{Schedule}"].Status);
        Assert.Equal(HealthStatus.Healthy, report.Entries[$"foundatio-cron-{legacyName}"].Status);
        Assert.Equal(HealthStatus.Healthy, report.Entries["legacy-wrapped"].Status);
        var unscheduled = report.Entries[$"foundatio-cron-{JobNames.Of<UnscheduledJob>()}"];
        Assert.Equal(HealthStatus.Unhealthy, unscheduled.Status);
        Assert.Contains($"No CRON schedule named \"{JobNames.Of<UnscheduledJob>()}\"", unscheduled.Description);
        Assert.Contains(legacyName, unscheduled.Description);
    }

    [Fact]
    public void AddFoundatioCronJob_WithAnInvalidWindow_Throws()
    {
        var builder = new ServiceCollection().AddHealthChecks();

        Assert.Throws<ArgumentOutOfRangeException>(() => builder.AddFoundatioCronJob(Schedule, TimeSpan.Zero));
        Assert.Throws<ArgumentException>(() => builder.AddFoundatioCronJob(" ", TimeSpan.FromMinutes(1)));
    }

    public override async ValueTask DisposeAsync()
    {
        await StopAsync();
        await base.DisposeAsync();
    }

    private async Task StopAsync()
    {
        foreach (var service in Enumerable.Reverse(_hostedServices))
            await service.StopAsync(CancellationToken.None);
        _hostedServices.Clear();
        foreach (var provider in _providers)
            await provider.DisposeAsync();
        _providers.Clear();
    }

    private async Task<ServiceProvider> StartAsync(bool runsSchedule, HealthStatus? failureStatus = null, string scheduleName = Schedule,
        string cron = "*/5 * * * * *", TimeSpan? succeededWithin = null, CronJobOptions? cronOptions = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(Log);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddFoundatioJobs(j => j
            .UseStore(_store)
            .Configure(o => { o.TimeProvider = _time; o.ShutdownTimeout = TimeSpan.Zero; })
            .AddCronJob<CountEventsJob>(cron, cronOptions ?? new CronJobOptions { RetainSucceededFor = TimeSpan.Zero }));
        services.AddHealthChecks().AddFoundatioCronJob(scheduleName, succeededWithin ?? TimeSpan.FromSeconds(15), name: Check, failureStatus: failureStatus);

        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        provider.GetRequiredService<JobsStatus>();
        if (runsSchedule)
        {
            foreach (var service in provider.GetServices<IHostedService>())
            {
                await service.StartAsync(TestCancellationToken);
                _hostedServices.Add(service);
            }
        }

        return provider;
    }

    private async Task<HealthReportEntry> CheckAsync(IServiceProvider provider)
    {
        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(r => r.Name == Check, TestCancellationToken);
        return report.Entries[Check];
    }

    private static string FormatWindow(TimeSpan window) => window.ToString("c", System.Globalization.CultureInfo.InvariantCulture);

    private async Task WaitForSucceededAsync(int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (await _store.CountAsync(JobState.Succeeded, Schedule, TestCancellationToken) < count)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Occurrence {count} did not succeed in time.");

            await Task.Delay(10, TestCancellationToken);
        }
    }

    [Job(Name = Schedule)]
    public sealed class CountEventsJob : IJobHandler
    {
        public Task HandleAsync(IJobContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public sealed class LegacyCountJob : IJob
    {
        public Task<JobResult> RunAsync(CancellationToken cancellationToken = default) => Task.FromResult(JobResult.Success);
    }

    public sealed class UnscheduledJob : IJobHandler
    {
        public Task HandleAsync(IJobContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
