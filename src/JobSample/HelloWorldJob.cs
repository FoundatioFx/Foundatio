using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;

namespace Foundatio.JobSample.Jobs {
    public class HelloWorldJob : JobBase {
        private readonly IAmADependency _dep;

        public HelloWorldJob(IAmADependency dep) {
            _dep = dep;
        }

        public int RunCount { get; set; }

        protected override Task<JobResult> RunInternalAsync(CancellationToken token) {
            RunCount++;

            Console.WriteLine("Hello World!");
            Thread.Sleep(100);

            return Task.FromResult(JobResult.Success);
        }
    }

    public interface IAmADependency {}

    public class MyDependency : IAmADependency { }
}
