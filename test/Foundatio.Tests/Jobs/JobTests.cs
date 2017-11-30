using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Jobs;
using Foundatio.Logging.Xunit;
using Foundatio.Metrics;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Jobs {
    public class JobTests : TestWithLoggingBase {
        public JobTests(ITestOutputHelper output) : base(output) {}

        [Fact]
        public async Task CanCancelJob() {
            var job = new HelloWorldJob();
            var token = TimeSpan.FromSeconds(1).ToCancellationToken();
            var resultTask = new JobRunner(job, Log).RunAsync(token);
            await SystemClock.SleepAsync(TimeSpan.FromSeconds(2));

            Assert.True(await resultTask);
        }

        [Fact]
        public async Task CanStopLongRunningJob() {
            var job = new LongRunningJob(Log);
            var runner = new JobRunner(job, Log);
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            bool result = await runner.RunAsync(cts.Token);
            
            Assert.True(result);
        }

        [Fact]
        public async Task CanStopLongRunningCronJob() {
            var job = new LongRunningJob(Log);
            var runner = new JobRunner(job, Log);
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            bool result = await runner.RunAsync(cts.Token);

            Assert.True(result);
        }

        [Fact]
        public async Task CanRunJobs() {
            var job = new HelloWorldJob();
            Assert.Equal(0, job.RunCount);
            await job.RunAsync();
            Assert.Equal(1, job.RunCount);

            job.RunContinuous(iterationLimit: 2);
            Assert.Equal(3, job.RunCount);

            var sw = Stopwatch.StartNew();
            job.RunContinuous(cancellationToken: TimeSpan.FromMilliseconds(100).ToCancellationToken());
            sw.Stop();
            Assert.InRange(sw.Elapsed, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(800));

            var jobInstance = new HelloWorldJob();
            Assert.NotNull(jobInstance);
            Assert.Equal(0, jobInstance.RunCount);
            Assert.Equal(JobResult.Success, await jobInstance.RunAsync());
            Assert.Equal(1, jobInstance.RunCount);
        }

        [Fact]
        public async Task CanRunMultipleInstances() {
            HelloWorldJob.GlobalRunCount = 0;
            
            var job = new HelloWorldJob();
            await new JobRunner(job, Log, instanceCount: 5, iterationLimit: 1).RunAsync(TimeSpan.FromSeconds(1).ToCancellationToken());
            Assert.Equal(5, HelloWorldJob.GlobalRunCount);

            HelloWorldJob.GlobalRunCount = 0;

            await new JobRunner(job, Log, instanceCount: 5, iterationLimit: 100).RunAsync(TimeSpan.FromSeconds(5).ToCancellationToken());
            Assert.Equal(500, HelloWorldJob.GlobalRunCount);
        }

        [Fact]
        public async Task CanCancelContinuousJobs() {
            using (TestSystemClock.Install()) {
                var job = new HelloWorldJob();
                job.RunContinuous(TimeSpan.FromSeconds(1), 5, TimeSpan.FromMilliseconds(100).ToCancellationToken());
                Assert.Equal(1, job.RunCount);

                var runnerTask = new JobRunner(job, Log, instanceCount: 5, iterationLimit: 10000, interval: TimeSpan.FromMilliseconds(1)).RunAsync(TimeSpan.FromMilliseconds(500).ToCancellationToken());
                await SystemClock.SleepAsync(TimeSpan.FromSeconds(1));
                await runnerTask;
            }
        }

        [Fact]
        public async Task CanRunJobsWithLocks() {
            var job = new WithLockingJob(Log);
            Assert.Equal(0, job.RunCount);
            await job.RunAsync();
            Assert.Equal(1, job.RunCount);

            job.RunContinuous(iterationLimit: 2);
            Assert.Equal(3, job.RunCount);

            await Run.InParallelAsync(2, i => job.RunAsync());
            Assert.Equal(4, job.RunCount);
        }

        [Fact]
        public async Task CanRunThrottledJobs() {
            using (var client = new InMemoryCacheClient(new InMemoryCacheClientOptions { LoggerFactory = Log })) {
                var jobs = new List<ThrottledJob>(new[] { new ThrottledJob(client, Log), new ThrottledJob(client, Log), new ThrottledJob(client, Log) });

                var sw = Stopwatch.StartNew();
                await Task.WhenAll(jobs.Select(job => Task.Run(() => job.RunContinuous(TimeSpan.FromMilliseconds(1), cancellationToken: TimeSpan.FromSeconds(1).ToCancellationToken()))));
                sw.Stop();
                Assert.InRange(jobs.Sum(j => j.RunCount), 4, 14);
                _logger.LogInformation(jobs.Sum(j => j.RunCount).ToString());
                Assert.InRange(sw.ElapsedMilliseconds, 20, 1500);
            }
        }

        [Fact(Skip = "Meant to be run manually.")]
        public async Task JobLoopPerf() {
            const int iterations = 10000;

            var metrics = new InMemoryMetricsClient(new InMemoryMetricsClientOptions { LoggerFactory = Log });
            var job = new SampleJob(metrics, Log);
            var sw = Stopwatch.StartNew();
            job.RunContinuous(null, iterations);
            sw.Stop();
            await metrics.FlushAsync();
            _logger.LogTrace((await metrics.GetCounterStatsAsync("runs")).ToString());
            _logger.LogTrace((await metrics.GetCounterStatsAsync("errors")).ToString());
            _logger.LogTrace((await metrics.GetCounterStatsAsync("failed")).ToString());
            _logger.LogTrace((await metrics.GetCounterStatsAsync("completed")).ToString());
        }
    }
}
