using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Extensions;
using Foundatio.Jobs;
using Foundatio.Lock;
using Xunit;

namespace Foundatio.Tests.Jobs {
    public class WithLockingJob : JobBase {
        private readonly ILockProvider _locker = new CacheLockProvider(new InMemoryCacheClient());
        public int RunCount { get; set; }

        protected override IDisposable GetJobLock() {
            return _locker.TryAcquireLock("WithLockingJob", TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(100));
        }

        protected override Task<JobResult> RunInternalAsync(CancellationToken token) {
            RunCount++;

            Thread.Sleep(50);
            Assert.True(_locker.IsLocked("WithLockingJob"));

            return Task.FromResult(JobResult.Success);
        }
    }
}
