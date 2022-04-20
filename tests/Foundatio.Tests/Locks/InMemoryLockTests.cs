using System;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Lock;
using Foundatio.Messaging;
using Xunit;
using Xunit.Abstractions;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Foundatio.Xunit;

namespace Foundatio.Tests.Locks {
    public class InMemoryLockTests : LockTestBase, IDisposable {
        private readonly ICacheClient _cache;
        private readonly IMessageBus _messageBus;

        public InMemoryLockTests(ITestOutputHelper output) : base(output) {
            _cache = new InMemoryCacheClient(o => o.LoggerFactory(Log));
            _messageBus = new InMemoryMessageBus(o => o.LoggerFactory(Log));
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
        public override Task CanAcquireMultipleResources() {
            return base.CanAcquireMultipleResources();
        }

        [Fact]
        public override Task CanAcquireMultipleScopedResources() {
            return base.CanAcquireMultipleScopedResources();
        }

        [RetryFact]
        public override Task WillThrottleCallsAsync() {
            Log.SetLogLevel<InMemoryCacheClient>(LogLevel.Trace);
            Log.SetLogLevel<InMemoryMessageBus>(LogLevel.Trace);

            return base.WillThrottleCallsAsync();
        }

        [Fact]
        public override Task CanReleaseLockMultipleTimes() {
            return base.CanReleaseLockMultipleTimes();
        }

        public void Dispose() {
            _cache.Dispose();
            _messageBus.Dispose();
        }
    }
}