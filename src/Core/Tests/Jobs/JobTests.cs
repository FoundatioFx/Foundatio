using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
    public class JobTests : CaptureTests
    {
        private TextWriter _writer;

        public JobTests(CaptureFixture fixture, ITestOutputHelper output) : base(fixture, output)
        {
            _writer = new TestOutputWriter(output);
        }

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
        public void CanRunJobs() {
            var job = new HelloWorldJob();
            Assert.Equal(0, job.RunCount);
            job.Run();
            Assert.Equal(1, job.RunCount);

            job.RunContinuous(iterationLimit: 2);
            Assert.Equal(3, job.RunCount);

            var sw = new Stopwatch();
            sw.Start();
            job.RunContinuous(token: new CancellationTokenSource(TimeSpan.FromMilliseconds(100)).Token);
            sw.Stop();
            Assert.InRange(sw.Elapsed, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(150));

            var jobInstance = JobRunner.CreateJobInstance(typeof(HelloWorldJob).AssemblyQualifiedName);
            Assert.NotNull(jobInstance);
            Assert.Equal(0, ((HelloWorldJob)jobInstance).RunCount);
            Assert.Equal(JobResult.Success, jobInstance.Run());
            Assert.Equal(1, ((HelloWorldJob)jobInstance).RunCount);
        }

        [Fact]
        public async void CanRunMultipleInstances()
        {
            HelloWorldJob.GlobalRunCount = 0;
            
            var tokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await JobRunner.RunContinuousAsync(typeof(HelloWorldJob), null, 5, 1, tokenSource.Token);
            Assert.Equal(5, HelloWorldJob.GlobalRunCount);

            HelloWorldJob.GlobalRunCount = 0;

            tokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await JobRunner.RunContinuousAsync(typeof(HelloWorldJob), null, 100, 5, tokenSource.Token);
            Assert.Equal(500, HelloWorldJob.GlobalRunCount);
        }

        [Fact]
        public async void CanCancelContinuousJobs()
        {
            var job = new HelloWorldJob();
            var tokenSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            job.RunContinuous(TimeSpan.FromSeconds(1), 5, tokenSource.Token);
            Assert.Equal(1, job.RunCount);

            tokenSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            await JobRunner.RunContinuousAsync(typeof(HelloWorldJob), instanceCount: 5, iterationLimit: 10000, cancellationToken: tokenSource.Token, interval: TimeSpan.FromMilliseconds(1));
        }

        [Fact]
        public void CanRunJobsWithLocks() {
            var job = new WithLockingJob();
            Assert.Equal(0, job.RunCount);
            job.Run();
            Assert.Equal(1, job.RunCount);

            job.RunContinuous(iterationLimit: 2);
            Assert.Equal(3, job.RunCount);

            Task.Run(() => job.Run());
            Task.Run(() => job.Run());
            Thread.Sleep(200);
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
            await Task.WhenAll(jobs.Select(
                async job => await job.RunContinuousAsync(TimeSpan.FromMilliseconds(1), cancellationToken: tokenSource.Token))
            );

            Assert.InRange(jobs.Sum(j => j.RunCount), 6, 14);
        }

        [Fact]
        public void CanBootstrapJobs() {
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

            var result = jobInstance.Run();
            Assert.Equal(true, result.IsSuccess);
            Assert.True(jobInstance is HelloWorldJob);
        }

        [Fact]
        public void CanRunQueueJob() {
            const int workItemCount = 10000;
            var queue = new InMemoryQueue<SampleQueueWorkItem>(0, TimeSpan.Zero);

            for (int i = 0; i < workItemCount; i++)
                queue.Enqueue(new SampleQueueWorkItem { Created = DateTime.Now, Path = "somepath" + i });

            var metrics = new InMemoryMetricsClient();
            var job = new SampleQueueJob(queue, metrics);
            job.RunUntilEmpty(new CancellationTokenSource(30000).Token);
            metrics.DisplayStats(_writer);

            Assert.Equal(0, queue.GetQueueStats().Queued);
        }

        [Fact]
        public void JobLoopPerf()
        {
            const int iterations = 10000;

            var metrics = new InMemoryMetricsClient();
            var job = new SampleJob(metrics);
            job.RunContinuous(null, iterations);
            metrics.DisplayStats(_writer);
        }
    }
}
