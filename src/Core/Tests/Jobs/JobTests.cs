#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Extensions;
using Foundatio.Jobs;
using Foundatio.Metrics;
using Foundatio.Queues;
using Foundatio.ServiceProviders;
using Foundatio.Tests.Utility;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Jobs {
    public class JobTests : CaptureTests {
        public JobTests(CaptureFixture fixture, ITestOutputHelper output) : base(fixture, output) {}

        [Fact]
        public void CanCancelJob()
        {
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            var result = JobRunner.RunAsync(new JobRunOptions
            {
                JobTypeName = typeof(HelloWorldJob).AssemblyQualifiedName,
                InstanceCount = 1,
                Interval = null,
                RunContinuous = true
            }, cts.Token).Result;

            Assert.Equal(0, result);
        }

        [Fact]
        public async Task CanRunJobs() {
            var job = new HelloWorldJob();
            Assert.Equal(0, job.RunCount);
            await job.RunAsync().AnyContext();
            Assert.Equal(1, job.RunCount);

            await job.RunContinuousAsync(iterationLimit: 2).AnyContext();
            Assert.Equal(3, job.RunCount);

            var sw = new Stopwatch();
            sw.Start();
            await job.RunContinuousAsync(cancellationToken: new CancellationTokenSource(TimeSpan.FromMilliseconds(100)).Token).AnyContext();
            sw.Stop();
            Assert.InRange(sw.Elapsed, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(150));

            var jobInstance = JobRunner.CreateJobInstance(typeof(HelloWorldJob).AssemblyQualifiedName);
            Assert.NotNull(jobInstance);
            Assert.Equal(0, ((HelloWorldJob)jobInstance).RunCount);
            Assert.Equal(JobResult.Success, await jobInstance.RunAsync().AnyContext());
            Assert.Equal(1, ((HelloWorldJob)jobInstance).RunCount);
        }

        [Fact]
        public async void CanRunMultipleInstances() {
            HelloWorldJob.GlobalRunCount = 0;
            
            var tokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await JobRunner.RunContinuousAsync(typeof(HelloWorldJob), null, 5, 1, tokenSource.Token).AnyContext();
            Assert.Equal(5, HelloWorldJob.GlobalRunCount);

            HelloWorldJob.GlobalRunCount = 0;

            tokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await JobRunner.RunContinuousAsync(typeof(HelloWorldJob), null, 100, 5, tokenSource.Token).AnyContext();
            Assert.Equal(500, HelloWorldJob.GlobalRunCount);
        }

        [Fact]
        public async void CanCancelContinuousJobs() {
            var job = new HelloWorldJob();
            var tokenSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await job.RunContinuousAsync(TimeSpan.FromSeconds(1), 5, tokenSource.Token).AnyContext();
            Assert.Equal(1, job.RunCount);

            tokenSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            await JobRunner.RunContinuousAsync(typeof(HelloWorldJob), instanceCount: 5, iterationLimit: 10000, cancellationToken: tokenSource.Token, interval: TimeSpan.FromMilliseconds(1)).AnyContext();
        }

        [Fact]
        public async Task CanRunJobsWithLocks() {
            var job = new WithLockingJob();
            Assert.Equal(0, job.RunCount);
            await job.RunAsync().AnyContext();
            Assert.Equal(1, job.RunCount);

            await job.RunContinuousAsync(iterationLimit: 2).AnyContext();
            Assert.Equal(3, job.RunCount);

            Task.Run(async () => await job.RunAsync().AnyContext());
            Task.Run(async () => await job.RunAsync().AnyContext());
            await Task.Delay(200).AnyContext();
            Assert.Equal(4, job.RunCount);
        }

        [Fact]
        public async void CanRunThrottledJobs() {
            var client = new InMemoryCacheClient();
            var jobs = new List<ThrottledJob>(new[] {
                new ThrottledJob(client),
                new ThrottledJob(client),
                new ThrottledJob(client)
            });

            var tokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await Task.WhenAll(jobs.Select(async job => await job.RunContinuousAsync(TimeSpan.FromMilliseconds(1), cancellationToken: tokenSource.Token).AnyContext())).AnyContext();

            Assert.InRange(jobs.Sum(j => j.RunCount), 6, 14);
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

            var result = await jobInstance.RunAsync().AnyContext();
            Assert.Equal(true, result.IsSuccess);
            Assert.True(jobInstance is HelloWorldJob);
        }

        [Fact]
        public async Task CanRunQueueJob() {
            const int workItemCount = 10000;
            var queue = new InMemoryQueue<SampleQueueWorkItem>(0, TimeSpan.Zero);

            for (int i = 0; i < workItemCount; i++)
                await queue.EnqueueAsync(new SampleQueueWorkItem {
                    Created = DateTime.Now,
                    Path = "somepath" + i
                }).AnyContext();

            var metrics = new InMemoryMetricsClient();
            var job = new SampleQueueJob(queue, metrics);
            await job.RunUntilEmptyAsync(new CancellationTokenSource(30000).Token).AnyContext();
            metrics.DisplayStats(_writer);

            Assert.Equal(0, (await queue.GetQueueStatsAsync().AnyContext()).Queued);
        }

        [Fact]
        public async Task JobLoopPerf() {
            const int iterations = 10000;

            var metrics = new InMemoryMetricsClient();
            var job = new SampleJob(metrics);
            await job.RunContinuousAsync(null, iterations).AnyContext();
            metrics.DisplayStats(_writer);
        }
    }
}

#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed