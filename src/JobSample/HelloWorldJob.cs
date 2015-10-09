using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Jobs;

namespace Foundatio.JobSample.Jobs {
    public class HelloWorldJob : JobBase {
        private readonly IAmADependency _dep;

        public HelloWorldJob(IAmADependency dep) {
            _dep = dep;
        }

        public int RunCount { get; set; }

        protected override async Task<JobResult> RunInternalAsync(JobRunContext context) {
            RunCount++;

            Console.WriteLine("Hello World!");
            await Task.Delay(100, context.CancellationToken).AnyContext();

            return JobResult.Success;
        }
    }

    public interface IAmADependency {}

    public class MyDependency : IAmADependency { }
}
