using System;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Extensions;
using Foundatio.Jobs;
using Foundatio.Lock;
using Foundatio.Logging;
using Foundatio.Messaging;
using Xunit;

namespace Foundatio.Tests.Jobs {
    public class WithLockingJob : JobBase {
        private readonly ILockProvider _locker;

        public WithLockingJob(ILoggerFactory loggerFactory) : base(loggerFactory) {
            _locker = new CacheLockProvider(new InMemoryCacheClient(loggerFactory), new InMemoryMessageBus(loggerFactory), loggerFactory);
        }

        public int RunCount { get; set; }

        protected override Task<ILock> GetJobLockAsync(){
            return _locker.AcquireAsync(nameof(WithLockingJob), TimeSpan.FromSeconds(1), TimeSpan.Zero);
        }

        protected override async Task<JobResult> RunInternalAsync(JobRunContext context) {
            RunCount++;

            await Task.Delay(150, context.CancellationToken).AnyContext();
            Assert.True(await _locker.IsLockedAsync("WithLockingJob").AnyContext());

            return JobResult.Success;
        }
    }
}
