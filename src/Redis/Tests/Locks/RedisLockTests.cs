using Foundatio.Lock;
using Foundatio.Caching;
using Foundatio.Tests;
using Xunit;

namespace Foundatio.Redis.Tests.Locks {
    public class RedisLockTests : LockTestBase {
        protected override ILockProvider GetLockProvider() {
            return new CacheLockProvider(new RedisCacheClient(SharedConnection.GetMuxer()));
        }

        [Fact]
        public override void CanAcquireAndReleaseLock() {
            base.CanAcquireAndReleaseLock();
        }

        [Fact]
        public override void LockWillTimeout() {
            base.LockWillTimeout();
        }
    }
}
