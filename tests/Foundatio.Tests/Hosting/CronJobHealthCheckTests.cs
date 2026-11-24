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

    private async Task<ServiceProvider> StartAsync(bool runsSchedule, HealthStatus? failureStatus = null, string scheduleName = Schedule)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(Log);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddFoundatioJobs(j => j
            .UseStore(_store)
            .Configure(o => { o.TimeProvider = _time; o.ShutdownTimeout = TimeSpan.Zero; })
            .AddCronJob<CountEventsJob>("*/5 * * * * *", new CronJobOptions { RetainSucceededFor = TimeSpan.Zero }));
        services.AddHealthChecks().AddFoundatioCronJob(scheduleName, TimeSpan.FromSeconds(15), name: Check, failureStatus: failureStatus);

        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
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
}
