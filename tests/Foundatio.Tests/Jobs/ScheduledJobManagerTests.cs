using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Foundatio.Tests.Jobs;

public class ScheduledJobManagerTests
{
    [Fact]
    public async Task ScheduleAsync_AddsAndReplacesByNameAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (manager, _, _) = CreateRuntime();

        await manager.ScheduleAsync(new ScheduledJobDefinition { Name = "nightly", Cron = "0 3 * * *", JobType = typeof(ProbeJob) }, cancellationToken);
        Assert.Equal("0 3 * * *", (await manager.GetScheduleAsync("nightly", cancellationToken))!.Cron);

        // Re-scheduling the same name replaces the whole definition (runtime add/update, no restart).
        await manager.ScheduleAsync(new ScheduledJobDefinition { Name = "nightly", Cron = "0 4 * * *", JobType = typeof(ProbeJob), MaxAttempts = 7 }, cancellationToken);
        var updated = await manager.GetScheduleAsync("nightly", cancellationToken);
        Assert.Equal("0 4 * * *", updated!.Cron);
        Assert.Equal(7, updated.MaxAttempts);
        Assert.Single(await manager.GetSchedulesAsync(cancellationToken));
    }

    [Fact]
    public async Task RescheduleAsync_ChangesCronAndValidatesAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (manager, _, _) = CreateRuntime();
        await manager.ScheduleAsync(new ScheduledJobDefinition { Name = "nightly", Cron = "0 3 * * *", JobType = typeof(ProbeJob), MaxAttempts = 5 }, cancellationToken);

        Assert.True(await manager.RescheduleAsync("nightly", "*/5 * * * *", cancellationToken));
        var updated = await manager.GetScheduleAsync("nightly", cancellationToken);
        Assert.Equal("*/5 * * * *", updated!.Cron);
        Assert.Equal(5, updated.MaxAttempts); // only the cron changed; the rest of the definition is preserved

        Assert.False(await manager.RescheduleAsync("unknown", "*/5 * * * *", cancellationToken));
        await Assert.ThrowsAnyAsync<Exception>(() => manager.RescheduleAsync("nightly", "not-a-cron", cancellationToken));
        Assert.Equal("*/5 * * * *", (await manager.GetScheduleAsync("nightly", cancellationToken))!.Cron); // invalid input changed nothing
    }

    [Fact]
    public async Task SetEnabledAsync_StopsAndResumesOccurrenceMaterializationAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (manager, processor, _) = CreateRuntime();
        await manager.ScheduleAsync(new ScheduledJobDefinition { Name = "everyminute", Cron = "* * * * *", JobType = typeof(ProbeJob) }, cancellationToken);

        var tick = new DateTimeOffset(2026, 1, 1, 0, 0, 30, TimeSpan.Zero);

        Assert.True(await manager.SetEnabledAsync("everyminute", false, cancellationToken));
        Assert.Empty(await processor.EnqueueDueOccurrencesAsync(tick, cancellationToken)); // disabled -> nothing materializes

        Assert.True(await manager.SetEnabledAsync("everyminute", true, cancellationToken));
        Assert.Single(await processor.EnqueueDueOccurrencesAsync(tick, cancellationToken)); // re-enabled -> occurrence materializes

        Assert.False(await manager.SetEnabledAsync("unknown", true, cancellationToken));
    }

    [Fact]
    public async Task TriggerAsync_RunsImmediatelyWithArgumentsAndReturnsHandleAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (manager, processor, probe) = CreateRuntime();

        // A schedule that would never fire on its own within the test (yearly), with typed arguments.
        await manager.ScheduleAsync(new ScheduledJobDefinition
        {
            Name = "yearly-report",
            Cron = "0 0 1 1 *",
            JobType = typeof(ProbeJob),
            Arguments = new ReportArgs { Region = "emea" }
        }, cancellationToken);

        var handle = await manager.TriggerAsync("yearly-report", cancellationToken);
        Assert.StartsWith("yearly-report:manual:", handle.JobId);

        // The trigger is durable: the pump's normal drain claims and runs it.
        Assert.Equal(1, await processor.RunDueOccurrencesAsync(DateTimeOffset.UtcNow, cancellationToken: cancellationToken));
        Assert.Equal("emea", probe.LastRegion);

        var state = await handle.GetStateAsync(cancellationToken);
        Assert.Equal(JobStatus.Completed, state!.Status);

        // A second trigger runs again (manual occurrences never dedupe).
        await manager.TriggerAsync("yearly-report", cancellationToken);
        Assert.Equal(1, await processor.RunDueOccurrencesAsync(DateTimeOffset.UtcNow, cancellationToken: cancellationToken));
        Assert.Equal(2, probe.RunCount);
    }

    [Fact]
    public async Task GenericOverloads_ResolveTheTypeDefaultNameAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (manager, processor, probe) = CreateRuntime();

        // Registered the way AddCronJob<TJob> does when no explicit name is given: the type's default name.
        await manager.ScheduleAsync(new ScheduledJobDefinition
        {
            Name = ScheduledJobDefinition.DefaultNameFor(typeof(ProbeJob)),
            Cron = "0 0 1 1 *",
            JobType = typeof(ProbeJob)
        }, cancellationToken);

        var found = await manager.GetScheduleAsync<ProbeJob>(cancellationToken);
        Assert.NotNull(found);
        Assert.Equal(nameof(ProbeJob), found.Name);

        Assert.True(await manager.RescheduleAsync<ProbeJob>("*/10 * * * *", cancellationToken));
        Assert.Equal("*/10 * * * *", (await manager.GetScheduleAsync<ProbeJob>(cancellationToken))!.Cron);

        Assert.True(await manager.SetEnabledAsync<ProbeJob>(false, cancellationToken));
        await Assert.ThrowsAsync<ScheduledJobDisabledException>(() => manager.TriggerAsync<ProbeJob>(cancellationToken));
        Assert.True(await manager.SetEnabledAsync<ProbeJob>(true, cancellationToken));

        var handle = await manager.TriggerAsync<ProbeJob>(cancellationToken);
        Assert.StartsWith($"{nameof(ProbeJob)}:manual:", handle.JobId);
        Assert.Equal(1, await processor.RunDueOccurrencesAsync(DateTimeOffset.UtcNow, cancellationToken: cancellationToken));
        Assert.Equal(1, probe.RunCount);
        Assert.Equal(JobStatus.Completed, (await handle.GetStateAsync(cancellationToken))!.Status);

        await manager.UnscheduleAsync<ProbeJob>(cancellationToken);
        Assert.Null(await manager.GetScheduleAsync<ProbeJob>(cancellationToken));
    }

    [Fact]
    public async Task TriggerAsync_UnknownOrDisabled_ThrowsAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (manager, _, _) = CreateRuntime();

        var notFound = await Assert.ThrowsAsync<ScheduledJobNotFoundException>(() => manager.TriggerAsync("unknown", cancellationToken));
        Assert.Equal("unknown", notFound.Name);

        await manager.ScheduleAsync(new ScheduledJobDefinition { Name = "off", Cron = "* * * * *", JobType = typeof(ProbeJob), Enabled = false }, cancellationToken);
        var ex = await Assert.ThrowsAsync<ScheduledJobDisabledException>(() => manager.TriggerAsync("off", cancellationToken));
        Assert.Contains("disabled", ex.Message);
        Assert.Equal("off", ex.Name);
    }

    private static (IScheduledJobManager Manager, JobScheduleProcessor Processor, RegionProbe Probe) CreateRuntime()
    {
        var store = new InMemoryJobRuntimeStore();
        var scheduler = new InMemoryScheduledJobStore();
        var probe = new RegionProbe();
        var serviceProvider = new ServiceCollection().AddSingleton(probe).BuildServiceProvider();
        var worker = new JobWorker(store, serviceProvider, nodeId: "node-a");
        var processor = new JobScheduleProcessor(scheduler, store, worker, nodeId: "node-a");
        var manager = new ScheduledJobManager(scheduler, store);
        return (manager, processor, probe);
    }

    private sealed class ReportArgs
    {
        public string? Region { get; set; }
    }

    private sealed class RegionProbe
    {
        private int _runCount;
        public int RunCount => Volatile.Read(ref _runCount);
        public string? LastRegion { get; private set; }

        public void Record(string? region)
        {
            Interlocked.Increment(ref _runCount);
            LastRegion = region;
        }
    }

    private sealed class ProbeJob : IJob
    {
        private readonly RegionProbe _probe;

        public ProbeJob(RegionProbe probe) => _probe = probe;

        public Task<JobResult> RunAsync(JobExecutionContext context)
        {
            _probe.Record(context.HasArguments ? context.GetArguments<ReportArgs>().Region : null);
            return Task.FromResult(JobResult.Success);
        }
    }
}
