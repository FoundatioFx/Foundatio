using System;
using System.Diagnostics;
using System.Threading;
using Foundatio.Dependency;
using Foundatio.Jobs;
using Xunit;

namespace Foundatio.Tests.Jobs {
    public class JobTests {
        [Fact]
        public void CanRunJobs() {
            var job = new HelloWorldJob();
            job.Run();
            Assert.Equal(1, job.RunCount);

            job.RunContinuous(iterationLimit: 2);
            Assert.Equal(3, job.RunCount);

            job.RunContinuous(token: new CancellationTokenSource(TimeSpan.FromMilliseconds(10)).Token);
            Assert.True(job.RunCount > 10);
        }

        [Fact]
        public void CanBootstrapJobs() {
            var resolver = JobRunner.GetResolver(typeof(JobTests));
            Assert.NotNull(resolver);

            var job = resolver.GetService<WithDependencyJob>();
            Assert.NotNull(job);
            Assert.NotNull(job.Dependency);
            Assert.Equal(5, job.Dependency.MyProperty);

            var jobInstance = JobRunner.CreateJobInstance("Foundatio.Tests.HelloWorldJob,Foundatio.Tests");
            Assert.NotNull(job);
            Assert.NotNull(job.Dependency);
            Assert.Equal(5, job.Dependency.MyProperty);

            int result = JobRunner.RunJob(jobInstance);
            Assert.Equal(0, result);
            Assert.True(jobInstance is HelloWorldJob);
        }
    }
}
