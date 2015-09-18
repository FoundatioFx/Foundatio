using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;

namespace Foundatio.Tests.Jobs {
    public class WithDependencyJob : JobBase {
        public WithDependencyJob(MyDependency dependency) {
            Dependency = dependency;
        }

        public MyDependency Dependency { get; private set; }

        public int RunCount { get; set; }

        protected override Task<JobResult> RunInternalAsync(CancellationToken cancellationToken) {
            RunCount++;

            return Task.FromResult(JobResult.Success);
        }
    }

    public class MyDependency {
        public int MyProperty { get; set; }
    }
}
