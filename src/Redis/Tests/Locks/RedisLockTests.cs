using System.Threading.Tasks;
using Foundatio.Lock;
using Foundatio.Caching;
using Foundatio.Tests.Utility;
using Xunit;
using Xunit.Abstractions;
using Foundatio.Messaging;
using Foundatio.Tests.Locks;

namespace Foundatio.Redis.Tests.Locks {
    public class RedisLockTests : LockTestBase {
        public RedisLockTests(CaptureFixture fixture, ITestOutputHelper output) : base(fixture, output) {}

        protected override ILockProvider GetLockProvider() {
            return new CacheLockProvider(new RedisCacheClient(SharedConnection.GetMuxer()), new RedisMessageBus(SharedConnection.GetMuxer().GetSubscriber()));
        }

        [Fact]
        public override Task CanAcquireAndReleaseLock() {
            return base.CanAcquireAndReleaseLock();
        }

        [Fact]
        public override Task LockWillTimeout() {
            return base.LockWillTimeout();
        }
    }
}
