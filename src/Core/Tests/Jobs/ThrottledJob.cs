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

        protected override Task<IDisposable> GetJobLockAsync() {
            return _locker.AcquireLockAsync("WithLockingJob", acquireTimeout: TimeSpan.Zero);
        }

        protected override Task<JobResult> RunInternalAsync(CancellationToken token) {
            RunCount++;

            return Task.FromResult(JobResult.Success);
        }
    }
}