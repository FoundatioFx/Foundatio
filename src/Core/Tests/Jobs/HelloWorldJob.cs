using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;

namespace Foundatio.Tests {
    public class HelloWorldJob : JobBase {
        public int RunCount { get; set; }

        protected override Task<JobResult> RunInternalAsync(CancellationToken token) {
            Console.WriteLine("Hello World! " + RunCount);
            RunCount++;

            return Task.FromResult(JobResult.Success);
        }
    }
}
