using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Jobs;
using Foundatio.Xunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Jobs;

public class JobTests : TestWithLoggingBase
{
    public JobTests(ITestOutputHelper output) : base(output) { }

    [Fact]
    public async Task CanCancelJob()
    {
        var job = new HelloWorldJob(null, Log);
        var sp = new ServiceCollection().BuildServiceProvider();
        var timeoutCancellationTokenSource = new CancellationTokenSource(1000);
        var resultTask = new JobRunner(job, sp, Log).RunAsync(timeoutCancellationTokenSource.Token);
        await TimeProvider.System.Delay(TimeSpan.FromSeconds(2));
        Assert.True(await resultTask);
    }

    [Fact]
    public async Task CanStopLongRunningJob()
    {
        var job = new LongRunningJob(null, Log);
        var sp = new ServiceCollection().BuildServiceProvider();
        var runner = new JobRunner(job, sp, Log);
        var cts = new CancellationTokenSource(1000);
        bool result = await runner.RunAsync(cts.Token);

        Assert.True(result);
    }

    [Fact]
    public async Task CanStopLongRunningCronJob()
    {
        var job = new LongRunningJob(null, Log);
        var sp = new ServiceCollection().BuildServiceProvider();
        var runner = new JobRunner(job, sp, Log);
        var cts = new CancellationTokenSource(1000);
        bool result = await runner.RunAsync(cts.Token);

        Assert.True(result);
    }

    [Fact]
    public async Task CanRunJobs()
    {
        var job = new HelloWorldJob(null, Log);
        Assert.Equal(0, job.RunCount);
        await job.RunAsync();
        Assert.Equal(1, job.RunCount);

        await job.RunContinuousAsync(iterationLimit: 2);
        Assert.Equal(3, job.RunCount);

        var sw = Stopwatch.StartNew();
        using (var timeoutCancellationTokenSource = new CancellationTokenSource(100))
        {
            await job.RunContinuousAsync(cancellationToken: timeoutCancellationTokenSource.Token);
        }
        sw.Stop();
        Assert.InRange(sw.Elapsed, TimeSpan.FromMilliseconds(95), TimeSpan.FromMilliseconds(800));

        var jobInstance = new HelloWorldJob(null, Log);
        Assert.NotNull(jobInstance);
        Assert.Equal(0, jobInstance.RunCount);
        Assert.Equal(JobResult.Success, await jobInstance.RunAsync());
        Assert.Equal(1, jobInstance.RunCount);
    }

    [Fact]
    public async Task CanRunMultipleInstances()
    {
        var job = new HelloWorldJob(null, Log);
        var sp = new ServiceCollection().BuildServiceProvider();

        HelloWorldJob.GlobalRunCount = 0;
        using (var timeoutCancellationTokenSource = new CancellationTokenSource(1000))
        {
            await new JobRunner(job, sp, Log, instanceCount: 5, iterationLimit: 1).RunAsync(timeoutCancellationTokenSource.Token);
        }

        Assert.Equal(5, HelloWorldJob.GlobalRunCount);

        HelloWorldJob.GlobalRunCount = 0;
        using (var timeoutCancellationTokenSource = new CancellationTokenSource(50000))
        {
            await new JobRunner(job, sp, Log, instanceCount: 5, iterationLimit: 100).RunAsync(timeoutCancellationTokenSource.Token);
        }

        Assert.Equal(500, HelloWorldJob.GlobalRunCount);
    }

    [Fact]
    public async Task CanCancelContinuousJobs()
    {
        var timeProvider = new FakeTimeProvider { AutoAdvanceAmount = TimeSpan.FromSeconds(1)};
        var job = new HelloWorldJob(timeProvider, Log);
        var sp = new ServiceCollection().AddSingleton<TimeProvider>(_ => timeProvider).BuildServiceProvider();
        var timeoutCancellationTokenSource = new CancellationTokenSource(100);
        await job.RunContinuousAsync(TimeSpan.FromSeconds(1), 5, timeoutCancellationTokenSource.Token);

        Assert.Equal(1, job.RunCount);

        timeoutCancellationTokenSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(50), timeProvider);
        var runnerTask = new JobRunner(job, sp, Log, instanceCount: 5, iterationLimit: 10000, interval: TimeSpan.FromMilliseconds(1)).RunAsync(timeoutCancellationTokenSource.Token);
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await runnerTask;
    }

    [Fact]
    public async Task CanRunJobsWithLocks()
    {
        var job = new WithLockingJob(Log);
        Assert.Equal(0, job.RunCount);
        await job.RunAsync();
        Assert.Equal(1, job.RunCount);

        await job.RunContinuousAsync(iterationLimit: 2);
        Assert.Equal(3, job.RunCount);

        await Parallel.ForEachAsync(Enumerable.Range(1, 2), async (_, ct) => await job.RunAsync(ct));
        Assert.Equal(4, job.RunCount);
    }

    [Fact]
    public async Task CanRunThrottledJobs()
    {
        using var client = new InMemoryCacheClient(o => o.LoggerFactory(Log));
        var jobs = new List<ThrottledJob>(new[] { new ThrottledJob(client, Log), new ThrottledJob(client, Log), new ThrottledJob(client, Log) });

        var sw = Stopwatch.StartNew();
        using var timeoutCancellationTokenSource = new CancellationTokenSource(1000);
        await Task.WhenAll(jobs.Select(job => job.RunContinuousAsync(TimeSpan.FromMilliseconds(1), cancellationToken: timeoutCancellationTokenSource.Token)));
        sw.Stop();

        Assert.InRange(jobs.Sum(j => j.RunCount), 4, 14);
        _logger.LogInformation(jobs.Sum(j => j.RunCount).ToString());
        Assert.InRange(sw.ElapsedMilliseconds, 20, 1500);
    }

    [Fact]
    public async Task CanRunJobsWithInterval()
    {
        var time = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(time);
        var interval = TimeSpan.FromHours(.75);

        var job = new HelloWorldJob(timeProvider, Log);

        var jobTask = Task.Run(() => job.RunContinuousAsync(iterationLimit: 2, interval: interval));
        while (job.RunCount < 1)
            await Task.Delay(10);
        timeProvider.Advance(interval);
        await jobTask;

        Assert.Equal(2, job.RunCount);
        Assert.Equal(interval, (timeProvider.GetUtcNow() - time));
    }

    [Fact]
    public async Task CanRunJobsWithIntervalBetweenFailingJob()
    {
        var time = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var interval = TimeSpan.FromHours(.75);
        var timeProvider = new FakeTimeProvider(time) { AutoAdvanceAmount = interval };

        var job = new FailingJob(timeProvider, Log);

        var jobTask = Task.Run(() => job.RunContinuousAsync(iterationLimit: 2, interval: interval));
        while (job.RunCount < 1)
            await Task.Delay(10);
        timeProvider.Advance(interval);
        await jobTask;

        Assert.Equal(2, job.RunCount);
        Assert.Equal(interval, (timeProvider.GetUtcNow() - time));
    }

    [Fact(Skip = "Meant to be run manually.")]
    public async Task JobLoopPerf()
    {
        const int iterations = 10000;

        var job = new SampleJob(null, Log);
        var sw = Stopwatch.StartNew();
        await job.RunContinuousAsync(null, iterations);
        sw.Stop();
    }
}
