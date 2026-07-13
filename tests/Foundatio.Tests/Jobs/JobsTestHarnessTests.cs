using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Jobs.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Foundatio.Tests.Jobs;

public class JobsTestHarnessTests
{
    [Fact]
    public async Task RunAllQueued_RunsEnqueuedJobsToCompletionAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (provider, probe) = CreateProvider();
        await using var _ = provider;
        var harness = provider.GetRequiredService<JobsTestHarness>();

        // The harness disables the auto pump, so nothing runs until the test says so.
        Assert.False(provider.GetRequiredService<JobRuntimePumpOptions>().Enabled);

        var handle = await harness.Client.EnqueueAsync<CounterJob>(cancellationToken: cancellationToken);
        Assert.Equal(JobStatus.Queued, (await harness.Monitor.GetAsync(handle.JobId, cancellationToken))!.Status);
        Assert.Equal(0, probe.RunCount);

        Assert.Equal(1, await harness.RunAllQueuedAsync(cancellationToken));
        Assert.Equal(1, probe.RunCount);
        Assert.Equal(JobStatus.Completed, (await handle.GetStateAsync(cancellationToken))!.Status);

        // Nothing left queued: a second pass is a no-op, not a re-run.
        Assert.Equal(0, await harness.RunAllQueuedAsync(cancellationToken));
        Assert.Equal(1, probe.RunCount);
    }

    [Fact]
    public async Task RunDue_MaterializesAndRunsTheCronOccurrenceAtAFixedNowAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (provider, probe) = CreateProvider();
        await using var _ = provider;
        var harness = provider.GetRequiredService<JobsTestHarness>();

        await harness.Schedules.ScheduleAsync(new ScheduledJobDefinition
        {
            Name = "every-minute",
            Cron = "* * * * *",
            JobType = typeof(CounterJob)
        }, cancellationToken);

        // One deterministic tick at a fixed "now": the 00:00:00 occurrence falls due within the misfire window and
        // runs in this call — no pump, no sleeps.
        var tick = new DateTimeOffset(2026, 1, 1, 0, 0, 30, TimeSpan.Zero);
        Assert.Equal(1, await harness.RunDueAsync(tick, cancellationToken));
        Assert.Equal(1, probe.RunCount);

        var occurrence = Assert.Single(await harness.Monitor.QueryAsync(new JobQuery { Name = "every-minute" }, cancellationToken));
        Assert.Equal(JobStatus.Completed, occurrence.Status);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), occurrence.ScheduledForUtc);

        // The same tick again is a no-op: the occurrence id dedupes and its dispatch was retired.
        Assert.Equal(0, await harness.RunDueAsync(tick, cancellationToken));
        Assert.Equal(1, probe.RunCount);
    }

    [Fact]
    public async Task RunToCompletion_RunsATypedArgsJobToItsTerminalStateAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (provider, probe) = CreateProvider();
        await using var _ = provider;
        var harness = provider.GetRequiredService<JobsTestHarness>();

        var handle = await harness.Client.EnqueueAsync<GreetingJob, GreetingArgs>(new GreetingArgs { Name = "ada" }, cancellationToken: cancellationToken);

        var state = await harness.RunToCompletionAsync(handle, cancellationToken);
        Assert.Equal(JobStatus.Completed, state.Status);
        Assert.Equal("ada", probe.LastGreeted);
    }

    private static (ServiceProvider Provider, Probe Probe) CreateProvider()
    {
        var probe = new Probe();
        var services = new ServiceCollection();
        services.AddSingleton(probe);
        services.AddFoundatio().Jobs.UseTestHarness();
        return (services.BuildServiceProvider(), probe);
    }

    private sealed class Probe
    {
        private int _runCount;
        public int RunCount => Volatile.Read(ref _runCount);
        public string? LastGreeted { get; private set; }

        public void Ran() => Interlocked.Increment(ref _runCount);
        public void Greeted(string? name) => LastGreeted = name;
    }

    private sealed class CounterJob : IJob
    {
        private readonly Probe _probe;

        public CounterJob(Probe probe) => _probe = probe;

        public Task<JobResult> RunAsync(JobExecutionContext context)
        {
            _probe.Ran();
            return Task.FromResult(JobResult.Success);
        }
    }

    private sealed class GreetingArgs
    {
        public string? Name { get; set; }
    }

    private sealed class GreetingJob : IJob
    {
        private readonly Probe _probe;

        public GreetingJob(Probe probe) => _probe = probe;

        public Task<JobResult> RunAsync(JobExecutionContext context)
        {
            _probe.Greeted(context.GetArguments<GreetingArgs>().Name);
            return Task.FromResult(JobResult.Success);
        }
    }
}
