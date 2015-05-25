using Foundatio.Lock;
using Foundatio.Caching;
using Foundatio.Tests;
using Foundatio.Tests.Utility;
using Xunit;
using Xunit.Abstractions;
using Foundatio.Messaging;

namespace Foundatio.Redis.Tests.Locks {
    public class RedisLockTests : LockTestBase {
        protected override ILockProvider GetLockProvider() {
            return new CacheLockProvider(new RedisCacheClient(SharedConnection.GetMuxer()), new RedisMessageBus(SharedConnection.GetMuxer().GetSubscriber()));
        }

        [Fact]
        public override void CanAcquireAndReleaseLock() {
            base.CanAcquireAndReleaseLock();
        }

        [Fact]
        public override void LockWillTimeout() {
            base.LockWillTimeout();
        }

        public RedisLockTests(CaptureFixture fixture, ITestOutputHelper output) : base(fixture, output) {}
    }
}
