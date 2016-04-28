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
    public class RedisLockTests : LockTestBase {
        public RedisLockTests(ITestOutputHelper output) : base(output) {
            FlushAll();
        }

        protected override ILockProvider GetThrottlingLockProvider(int maxHits, TimeSpan period) {
            return new ThrottlingLockProvider(new RedisCacheClient(SharedConnection.GetMuxer(), loggerFactory: Log), maxHits, period, Log);
        }

        protected override ILockProvider GetLockProvider() {
            return new CacheLockProvider(new RedisCacheClient(SharedConnection.GetMuxer(), loggerFactory: Log), new RedisMessageBus(SharedConnection.GetMuxer().GetSubscriber(), loggerFactory: Log), Log);
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
    }
}
