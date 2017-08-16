using System;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Lock;
using Foundatio.Messaging;
using Xunit;
using Xunit.Abstractions;
using Foundatio.Utility;

namespace Foundatio.Tests.Locks {
    public class InMemoryLockTests : LockTestBase, IDisposable {
        private readonly ICacheClient _cache;
        private readonly IMessageBus _messageBus;

        public InMemoryLockTests(ITestOutputHelper output) : base(output) {
            _cache = new InMemoryCacheClient(new InMemoryCacheClientOptions { LoggerFactory = Log });
            _messageBus = new InMemoryMessageBus(new InMemoryMessageBusOptions { LoggerFactory = Log });
        }

        protected override ILockProvider GetThrottlingLockProvider(int maxHits, TimeSpan period) {
            return new ThrottlingLockProvider(_cache, maxHits, period, Log);
        }

        protected override ILockProvider GetLockProvider() {
            return new CacheLockProvider(_cache, _messageBus, Log);
        }

        [Fact]
        public override Task CanAcquireAndReleaseLockAsync() {
            using (TestSystemClock.Install()) {
                return base.CanAcquireAndReleaseLockAsync();
            }
        }

        [Fact]
        public override Task LockWillTimeoutAsync() {
            return base.LockWillTimeoutAsync();
        }

        [Fact]
        public override Task LockOneAtATimeAsync() {
            return base.LockOneAtATimeAsync();
        }

        [Fact]
        public override Task WillThrottleCallsAsync() {
            return base.WillThrottleCallsAsync();
        }
        
        public void Dispose() {
            _cache.Dispose();
            _messageBus.Dispose();
        }
    }
}