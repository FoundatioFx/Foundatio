using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Extensions;
using Foundatio.Jobs;
using Foundatio.Logging;
using Foundatio.Logging.Xunit;
using Foundatio.Metrics;
using Foundatio.ServiceProviders;
using Foundatio.Utility;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Jobs {
    public class JobTests : TestWithLoggingBase {
        public JobTests(ITestOutputHelper output) : base(output) {
            SystemClock.Reset();
        }

        [Fact]
        public async Task CanCancelJob() {
            var token = TimeSpan.FromSeconds(1).ToCancellationToken();
            var job = new HelloWorldJob();
            var result = await new JobRunner(job, Log).RunAsync(token);

            Assert.True(result);
        }

        [Fact]
        public async Task CanRunJobs() {
            var job = new HelloWorldJob();
            Assert.Equal(0, job.RunCount);
            await job.RunAsync();
            Assert.Equal(1, job.RunCount);

            await job.RunContinuousAsync(iterationLimit: 2);
            Assert.Equal(3, job.RunCount);

            var sw = Stopwatch.StartNew();
            await job.RunContinuousAsync(cancellationToken: TimeSpan.FromMilliseconds(100).ToCancellationToken());
            sw.Stop();
            Assert.InRange(sw.Elapsed, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(250));

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
            var job = new HelloWorldJob();
            await job.RunContinuousAsync(TimeSpan.FromSeconds(1), 5, TimeSpan.FromMilliseconds(100).ToCancellationToken());
            Assert.Equal(1, job.RunCount);

            await new JobRunner(job, Log, instanceCount: 5, iterationLimit: 10000, interval: TimeSpan.FromMilliseconds(1))
                .RunAsync(TimeSpan.FromMilliseconds(500).ToCancellationToken());
        }

        [Fact]
        public async Task CanRunJobsWithLocks() {
            var job = new WithLockingJob(Log);
            Assert.Equal(0, job.RunCount);
            await job.RunAsync();
            Assert.Equal(1, job.RunCount);

            await job.RunContinuousAsync(iterationLimit: 2);
            Assert.Equal(3, job.RunCount);

            await Run.InParallel(2, async i => await job.RunAsync());
            Assert.Equal(4, job.RunCount);
        }

        [Fact]
        public async Task CanRunThrottledJobs() {
            using (var client = new InMemoryCacheClient()) {
                var jobs = new List<ThrottledJob>(new[] { new ThrottledJob(client, Log), new ThrottledJob(client, Log), new ThrottledJob(client, Log) });

                var sw = Stopwatch.StartNew();
                await Task.WhenAll(jobs.Select(async job => await job.RunContinuousAsync(TimeSpan.FromMilliseconds(1), cancellationToken: TimeSpan.FromSeconds(1).ToCancellationToken()).AnyContext()));
                sw.Stop();
                Assert.InRange(jobs.Sum(j => j.RunCount), 6, 14);
                _logger.Info(jobs.Sum(j => j.RunCount).ToString());
                Assert.InRange(sw.ElapsedMilliseconds, 20, 1500);
            }
        }

        [Fact(Skip = "Meant to be run manually.")]
        public async Task JobLoopPerf() {
            const int iterations = 10000;

            var metrics = new InMemoryMetricsClient();
            var job = new SampleJob(metrics, Log);
            var sw = Stopwatch.StartNew();
            await job.RunContinuousAsync(null, iterations);
            sw.Stop();
            await metrics.FlushAsync();
            _logger.Trace((await metrics.GetCounterStatsAsync("runs")).ToString());
            _logger.Trace((await metrics.GetCounterStatsAsync("errors")).ToString());
            _logger.Trace((await metrics.GetCounterStatsAsync("failed")).ToString());
            _logger.Trace((await metrics.GetCounterStatsAsync("completed")).ToString());
        }
    }
}