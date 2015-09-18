using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Logging;

namespace Foundatio.Tests.Jobs {
    public class HelloWorldJob : JobBase {
        private string _id;

        public HelloWorldJob() {
            _id = Guid.NewGuid().ToString("N").Substring(0, 10);
        }

        public static int GlobalRunCount;
        public int RunCount { get; set; }

        protected override Task<JobResult> RunInternalAsync(CancellationToken cancellationToken) {
            RunCount++;
            Interlocked.Increment(ref GlobalRunCount);

            Logger.Trace().Message("HelloWorld Running: instance={0} runs={1} global={2}", _id, RunCount, GlobalRunCount).Write();

            return Task.FromResult(JobResult.Success);
        }
    }
}