using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Logging;
using Microsoft.Extensions.Logging;

namespace Foundatio.Tests.Jobs {
    public class HelloWorldJob : JobBase {
        private readonly string _id;

        public HelloWorldJob(ILoggerFactory loggerFactory = null) : base(loggerFactory) {
            _id = Guid.NewGuid().ToString("N").Substring(0, 10);
        }

        public static int GlobalRunCount;
        public int RunCount { get; set; }

        protected override Task<JobResult> RunInternalAsync(JobRunContext context) {
            RunCount++;
            Interlocked.Increment(ref GlobalRunCount);

            _logger.Trace().Message("HelloWorld Running: instance={0} runs={1} global={2}", _id, RunCount, GlobalRunCount).Write();

            return Task.FromResult(JobResult.Success);
        }
    }
}