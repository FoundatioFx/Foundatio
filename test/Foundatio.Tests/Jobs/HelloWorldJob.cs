using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Logging;

namespace Foundatio.Tests.Jobs {
    public class HelloWorldJob : JobBase {
        private readonly string _id;

        public HelloWorldJob() : base(null) {
            _id = Guid.NewGuid().ToString("N").Substring(0, 10);
        }

        public static int GlobalRunCount;
        public int RunCount { get; set; }

        protected override Task<JobResult> RunInternalAsync(JobContext context) {
            RunCount++;
            Interlocked.Increment(ref GlobalRunCount);

            _logger.Trace("HelloWorld Running: instance={0} runs={1} global={2}", _id, RunCount, GlobalRunCount);

            return Task.FromResult(JobResult.Success);
        }
    }

    public class LongRunningJob : JobBase {
        private readonly string _id;
        private int _iterationCount;

        public LongRunningJob(ILoggerFactory loggerFactory) : base(loggerFactory) {
            _id = Guid.NewGuid().ToString("N").Substring(0, 10);
        }

        public int IterationCount => _iterationCount;

        protected override Task<JobResult> RunInternalAsync(JobContext context) {
            do {
                Interlocked.Increment(ref _iterationCount);
                if (context.CancellationToken.IsCancellationRequested)
                    break;
                
                if (_iterationCount % 10000 == 0)
                    _logger.Trace("LongRunningJob Running: instance={0} iterations={1}", _id, IterationCount);
            } while (true);

            return Task.FromResult(JobResult.Success);
        }
    }
}