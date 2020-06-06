using System;
using System.Threading.Tasks;
using Foundatio.Lock;
using Foundatio.Caching;
using Xunit;
using Xunit.Abstractions;
using Foundatio.Messaging;
using Foundatio.Redis.Tests.Extensions;
using Foundatio.Tests.Locks;
using Foundatio.Xunit;

namespace Foundatio.Redis.Tests.Locks {
    public class RedisLockTests : LockTestBase, IDisposable {
        private readonly ICacheClient _cache;
        private readonly IMessageBus _messageBus;

        public RedisLockTests(ITestOutputHelper output) : base(output) {
            var muxer = SharedConnection.GetMuxer();
            muxer.FlushAllAsync().GetAwaiter().GetResult();
            _cache = new RedisCacheClient(o => o.ConnectionMultiplexer(muxer).LoggerFactory(Log));
            _messageBus = new RedisMessageBus(o => o.Subscriber(muxer.GetSubscriber()).Topic("test-lock").LoggerFactory(Log));
        }

        protected override ILockProvider GetThrottlingLockProvider(int maxHits, TimeSpan period) {
            return new ThrottlingLockProvider(_cache, maxHits, period, Log);
        }

        protected override ILockProvider GetLockProvider() {
            return new CacheLockProvider(_cache, _messageBus, Log);
        }

        [Fact]
        public override Task CanAcquireAndReleaseLockAsync() {
            return base.CanAcquireAndReleaseLockAsync();
        }

        [Fact]
        public override Task LockWillTimeoutAsync() {
            return base.LockWillTimeoutAsync();
        }

        [RetryFact]
        public override Task WillThrottleCallsAsync() {
            return base.WillThrottleCallsAsync();
        }

        [Fact]
        public override Task LockOneAtATimeAsync() {
            return base.LockOneAtATimeAsync();
        }

        public void Dispose() {
            _cache.Dispose();
            _messageBus.Dispose();
            var muxer = SharedConnection.GetMuxer();
            muxer.FlushAllAsync().GetAwaiter().GetResult();
        }
    }
}
