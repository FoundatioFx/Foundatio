using System;
using System.Threading.Tasks;
using Foundatio.Lock;
using Foundatio.Caching;
using Foundatio.Logging;
using Xunit;
using Xunit.Abstractions;
using Foundatio.Messaging;
using Foundatio.Tests.Locks;

namespace Foundatio.Redis.Tests.Locks {
    public class RedisLockTests : LockTestBase, IDisposable {
        private readonly ICacheClient _cache;
        private readonly IMessageBus _messageBus;

        public RedisLockTests(ITestOutputHelper output) : base(output) {
            FlushAll();
            _cache = new RedisCacheClient(SharedConnection.GetMuxer(), loggerFactory: Log);
            _messageBus = new RedisMessageBus(SharedConnection.GetMuxer().GetSubscriber(), loggerFactory: Log);
        }

        protected override ILockProvider GetThrottlingLockProvider(int maxHits, TimeSpan period) {
            return new ThrottlingLockProvider(_cache, maxHits, period, Log);
        }

        protected override ILockProvider GetLockProvider() {
            return new CacheLockProvider(_cache, _messageBus, Log);
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

        private void FlushAll() {
            var endpoints = SharedConnection.GetMuxer().GetEndPoints(true);
            if (endpoints.Length == 0)
                return;

            foreach (var endpoint in endpoints) {
                var server = SharedConnection.GetMuxer().GetServer(endpoint);

                try {
                    server.FlushAllDatabases();
                } catch (Exception ex) {
                    _logger.Error(ex, "Error flushing redis");
                }
            }
        }
        
        public void Dispose() {
            _cache.Dispose();
            _messageBus.Dispose();
            FlushAll();
        }
    }
}
