using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Logging;

namespace Foundatio.Jobs {
    public abstract class JobBase : IJob, IHaveLogger {
        protected readonly ILogger _logger;

        public JobBase(ILoggerFactory loggerFactory = null) {
            _logger = loggerFactory.CreateLogger(GetType());
        }

        public string JobId { get; } = Guid.NewGuid().ToString("N").Substring(0, 10);
        ILogger IHaveLogger.Logger => _logger;

        public async Task<JobResult> RunAsync(CancellationToken cancellationToken = new CancellationToken()) {
            return await RunInternalAsync(new JobContext(cancellationToken)).AnyContext();
        }

        protected abstract Task<JobResult> RunInternalAsync(JobContext context);
    }
}