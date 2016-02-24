using System;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Jobs;
using Foundatio.Lock;
using Foundatio.Logging;

namespace Foundatio.Tests.Jobs {
    public class ThrottledJob : JobBase {
        public ThrottledJob(ICacheClient client, ILoggerFactory loggerFactory = null) : base(loggerFactory) {
            _locker = new ThrottlingLockProvider(client, 1, TimeSpan.FromMilliseconds(100), loggerFactory);
        }

        private readonly ILockProvider _locker;
        public int RunCount { get; set; }

        protected override Task<ILock> GetJobLockAsync() {
            return _locker.AcquireAsync(nameof(ThrottledJob), acquireTimeout: TimeSpan.Zero);
        }

        protected override Task<JobResult> RunInternalAsync(JobRunContext context) {
            RunCount++;

            return Task.FromResult(JobResult.Success);
        }
    }
}