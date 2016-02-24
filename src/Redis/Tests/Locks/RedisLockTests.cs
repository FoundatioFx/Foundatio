using System;
using System.Threading.Tasks;
using Foundatio.Lock;
using Foundatio.Caching;
using Xunit;
using Xunit.Abstractions;
using Foundatio.Messaging;
using Foundatio.Tests.Locks;

namespace Foundatio.Redis.Tests.Locks {
    public class RedisLockTests : LockTestBase {
        public RedisLockTests(ITestOutputHelper output) : base(output) {}

        protected override ILockProvider GetThrottlingLockProvider(int maxHits, TimeSpan period) {
            return new ThrottlingLockProvider(new RedisCacheClient(SharedConnection.GetMuxer()), maxHits, period);
        }

        protected override ILockProvider GetLockProvider() {
            return new CacheLockProvider(new RedisCacheClient(SharedConnection.GetMuxer()), new RedisMessageBus(SharedConnection.GetMuxer().GetSubscriber()), LoggerFactory);
        }

        [Fact]
        public override Task CanAcquireAndReleaseLock() {
            return base.CanAcquireAndReleaseLock();
        }

        [Fact]
        public override Task LockWillTimeout() {
            return base.LockWillTimeout();
        }

        [Fact]
        public override Task WillThrottleCalls() {
            return base.WillThrottleCalls();
        }

        [Fact]
        public override Task LockOneAtATime() {
            return base.LockOneAtATime();
        }
    }
}
