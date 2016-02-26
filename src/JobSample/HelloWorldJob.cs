using System;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Jobs;
using Foundatio.Logging;

namespace Foundatio.JobSample.Jobs {
    public class HelloWorldJob : JobBase {
        private readonly IAmADependency _dep;

        public HelloWorldJob(IAmADependency dep, ILoggerFactory loggerFactory) : base(loggerFactory) {
            _dep = dep;
        }

        public int RunCount { get; set; }

        protected override async Task<JobResult> RunInternalAsync(JobRunContext context) {
            RunCount++;

            _logger.Info("Hello World!");
            await Task.Delay(100, context.CancellationToken).AnyContext();

            return JobResult.Success;
        }
    }

    public interface IAmADependency {}

    public class MyDependency : IAmADependency { }
}
