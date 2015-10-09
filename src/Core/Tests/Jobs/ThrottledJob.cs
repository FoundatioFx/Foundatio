using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Jobs;
using Foundatio.Lock;

namespace Foundatio.Tests.Jobs {
    public class ThrottledJob : JobBase {
        public ThrottledJob(ICacheClient client) {
            _locker = new ThrottlingLockProvider(client, 1, TimeSpan.FromMilliseconds(100));
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