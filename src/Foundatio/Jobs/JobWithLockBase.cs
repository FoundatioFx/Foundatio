using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Lock;
using Foundatio.Logging;

namespace Foundatio.Jobs {
    public abstract class JobWithLockBase : IJob, IHaveLogger {
        protected readonly ILogger _logger;

        public JobWithLockBase(ILoggerFactory loggerFactory = null) {
            _logger = loggerFactory.CreateLogger(GetType());
        }

        public string JobId { get; } = Guid.NewGuid().ToString("N").Substring(0, 10);
        ILogger IHaveLogger.Logger => _logger;

        public async Task<JobResult> RunAsync(CancellationToken cancellationToken = new CancellationToken()) {
            var lockValue = await GetLockAsync(cancellationToken).AnyContext();
            if (lockValue == null) {
                _logger.Trace("Unable to acquire job lock.");
                return JobResult.Success;
            }

            try {
                return await RunInternalAsync(new JobContext(cancellationToken, lockValue)).AnyContext();
            } finally {
                await lockValue.ReleaseAsync().AnyContext();
            }
        }

        protected abstract Task<JobResult> RunInternalAsync(JobContext context);

        protected abstract Task<ILock> GetLockAsync(CancellationToken cancellationToken = default(CancellationToken));
    }
}