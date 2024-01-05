using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Lock;
using Foundatio.Messaging;
using Foundatio.Redis.Tests.Extensions;
using Foundatio.Tests.Locks;
using Foundatio.Utility;
using Foundatio.Xunit;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Redis.Tests.Locks
{
    public class RedisLockTests : LockTestBase, IDisposable
    {
        private readonly ICacheClient _cache;
        private readonly IMessageBus _messageBus;

        public RedisLockTests(ITestOutputHelper output) : base(output)
        {
            var muxer = SharedConnection.GetMuxer(Log);
            muxer.FlushAllAsync().GetAwaiter().GetResult();
            _cache = new RedisCacheClient(o => o.ConnectionMultiplexer(muxer).LoggerFactory(Log));
            _messageBus = new RedisMessageBus(o => o.Subscriber(muxer.GetSubscriber()).Topic("test-lock").LoggerFactory(Log));
        }

        protected override ILockProvider GetThrottlingLockProvider(int maxHits, TimeSpan period)
        {
            return new ThrottlingLockProvider(_cache, maxHits, period, Log);
        }

        protected override ILockProvider GetLockProvider()
        {
            return new CacheLockProvider(_cache, _messageBus, Log);
        }

        [Fact]
        public override Task CanAcquireLocksInParallel()
        {
            return base.CanAcquireLocksInParallel();
        }

        [Fact]
        public override Task CanAcquireAndReleaseLockAsync()
        {
            return base.CanAcquireAndReleaseLockAsync();
        }

        [Fact]
        public override Task LockWillTimeoutAsync()
        {
            return base.LockWillTimeoutAsync();
        }

        [Fact]
        public async Task LockWontTimeoutEarly()
        {
            Log.SetLogLevel<InMemoryCacheClient>(LogLevel.Trace);
            Log.SetLogLevel<CacheLockProvider>(LogLevel.Trace);
            Log.SetLogLevel<ScheduledTimer>(LogLevel.Trace);

            var locker = GetLockProvider();
            if (locker == null)
                return;

            _logger.LogInformation("Acquiring lock #1");
            var testLock = await locker.AcquireAsync("test", timeUntilExpires: TimeSpan.FromSeconds(1));
            _logger.LogInformation(testLock != null ? "Acquired lock #1" : "Unable to acquire lock #1");
            Assert.NotNull(testLock);

            _logger.LogInformation("Acquiring lock #2");
            var testLock2 = await locker.AcquireAsync("test", acquireTimeout: TimeSpan.FromMilliseconds(500));
            Assert.Null(testLock2);

            _logger.LogInformation("Renew lock #1");
            await testLock.RenewAsync(timeUntilExpires: TimeSpan.FromSeconds(1));

            _logger.LogInformation("Acquiring lock #3");
            testLock = await locker.AcquireAsync("test", acquireTimeout: TimeSpan.FromMilliseconds(500));
            Assert.Null(testLock);

            var sw = Stopwatch.StartNew();
            _logger.LogInformation("Acquiring lock #4");
            testLock = await locker.AcquireAsync("test", acquireTimeout: TimeSpan.FromSeconds(5));
            sw.Stop();
            _logger.LogInformation(testLock != null ? "Acquired lock #3" : "Unable to acquire lock #4");
            Assert.NotNull(testLock);
            Assert.True(sw.ElapsedMilliseconds > 400);
        }

        [RetryFact]
        public override Task WillThrottleCallsAsync()
        {
            return base.WillThrottleCallsAsync();
        }

        [Fact]
        public override Task LockOneAtATimeAsync()
        {
            return base.LockOneAtATimeAsync();
        }

        public void Dispose()
        {
            _cache.Dispose();
            _messageBus.Dispose();
            var muxer = SharedConnection.GetMuxer(Log);
            muxer.FlushAllAsync().GetAwaiter().GetResult();
        }
    }
}
