using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Extensions;
using Foundatio.Jobs;
using Foundatio.Metrics;
using Foundatio.ServiceProviders;
using Foundatio.Tests.Utility;
using Foundatio.Utility;
using Xunit;
using Xunit.Abstractions;
using TaskExtensions = Foundatio.Extensions.TaskExtensions;

namespace Foundatio.Tests.Jobs {
    public class JobTests : CaptureTests {
        public JobTests(CaptureFixture fixture, ITestOutputHelper output) : base(fixture, output) {}

        [Fact]
        public async Task CanCancelJob() {
            var token = TimeSpan.FromSeconds(1).ToCancellationToken();
            var result = await JobRunner.RunAsync(new JobRunOptions {
                JobTypeName = typeof(HelloWorldJob).AssemblyQualifiedName,
                InstanceCount = 1,
                Interval = null,
                RunContinuous = true
            }, token);

            Assert.Equal(0, result);
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
            Assert.InRange(sw.Elapsed, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(150));

            var jobInstance = JobRunner.CreateJobInstance(typeof(HelloWorldJob).AssemblyQualifiedName);
            Assert.NotNull(jobInstance);
            Assert.Equal(0, ((HelloWorldJob)jobInstance).RunCount);
            Assert.Equal(JobResult.Success, await jobInstance.RunAsync());
            Assert.Equal(1, ((HelloWorldJob)jobInstance).RunCount);
        }

        [Fact]
        public async Task CanRunMultipleInstances() {
            HelloWorldJob.GlobalRunCount = 0;
            
            await JobRunner.RunContinuousAsync(typeof(HelloWorldJob), null, null, 5, 1, TimeSpan.FromSeconds(1).ToCancellationToken());
            Assert.Equal(5, HelloWorldJob.GlobalRunCount);

            HelloWorldJob.GlobalRunCount = 0;
            
            await JobRunner.RunContinuousAsync(typeof(HelloWorldJob), null, null, 100, 5, TimeSpan.FromSeconds(5).ToCancellationToken());
            Assert.Equal(500, HelloWorldJob.GlobalRunCount);
        }

        [Fact]
        public async Task CanCancelContinuousJobs() {
            var job = new HelloWorldJob();
            await job.RunContinuousAsync(TimeSpan.FromSeconds(1), 5, TimeSpan.FromMilliseconds(100).ToCancellationToken());
            Assert.Equal(1, job.RunCount);

            await JobRunner.RunContinuousAsync(typeof(HelloWorldJob), instanceCount: 5, iterationLimit: 10000, cancellationToken: TimeSpan.FromMilliseconds(500).ToCancellationToken(), interval: TimeSpan.FromMilliseconds(1));
        }

        [Fact]
        public async Task CanRunJobsWithLocks() {
            var job = new WithLockingJob();
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
            var client = new InMemoryCacheClient();
            var jobs = new List<ThrottledJob>(new[] { new ThrottledJob(client), new ThrottledJob(client), new ThrottledJob(client) });
            
            var sw = Stopwatch.StartNew();
            await Task.WhenAll(jobs.Select(async job => await TaskExtensions.AnyContext(job.RunContinuousAsync(TimeSpan.FromMilliseconds(1), cancellationToken: TimeSpan.FromSeconds(1).ToCancellationToken()))));
            sw.Stop();
            Assert.InRange(jobs.Sum(j => j.RunCount), 6, 14);
            Assert.InRange(sw.ElapsedMilliseconds, 20, 1500);
        }

        [Fact]
        public async Task CanBootstrapJobs() {
            ServiceProvider.SetServiceProvider(typeof(JobTests));
            Assert.NotNull(ServiceProvider.Current);
            Assert.Equal(ServiceProvider.Current.GetType(), typeof(MyBootstrappedServiceProvider));

            ServiceProvider.SetServiceProvider(typeof(MyBootstrappedServiceProvider));
            Assert.NotNull(ServiceProvider.Current);
            Assert.Equal(ServiceProvider.Current.GetType(), typeof(MyBootstrappedServiceProvider));

            var job = ServiceProvider.Current.GetService<WithDependencyJob>();
            Assert.NotNull(job);
            Assert.NotNull(job.Dependency);
            Assert.Equal(5, job.Dependency.MyProperty);

            var jobInstance = JobRunner.CreateJobInstance("Foundatio.Tests.Jobs.HelloWorldJob,Foundatio.Tests");
            Assert.NotNull(job);
            Assert.NotNull(job.Dependency);
            Assert.Equal(5, job.Dependency.MyProperty);

            ServiceProvider.SetServiceProvider("Foundatio.Tests.Jobs.MyBootstrappedServiceProvider,Foundatio.Tests", "Foundatio.Tests.Jobs.HelloWorldJob,Foundatio.Tests");
            jobInstance = JobRunner.CreateJobInstance("Foundatio.Tests.Jobs.HelloWorldJob,Foundatio.Tests");
            Assert.NotNull(job);
            Assert.NotNull(job.Dependency);
            Assert.Equal(5, job.Dependency.MyProperty);

            var result = await jobInstance.RunAsync();
            Assert.Equal(true, result.IsSuccess);
            Assert.True(jobInstance is HelloWorldJob);
        }

        [Fact]
        public async Task JobLoopPerf() {
            const int iterations = 10000;

            var metrics = new InMemoryMetricsClient();
            var job = new SampleJob(metrics);
            await job.RunContinuousAsync(null, iterations);
            metrics.DisplayStats(_writer);
        }
    }
}