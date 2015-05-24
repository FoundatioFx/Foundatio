using System;
using System.Diagnostics;
using System.Threading;
using Foundatio.Caching;
using Foundatio.Extensions;
using Foundatio.Lock;
using Foundatio.Tests;
using Foundatio.Tests.Utility;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Redis.Tests {
    public class RedisThrottlingLockTests : LockTestBase {
        private readonly TimeSpan _period = TimeSpan.FromSeconds(3);

        protected override ILockProvider GetLockProvider() {
            return new ThrottlingLockProvider(new RedisCacheClient(SharedConnection.GetMuxer()), 5, _period);
        }

        [Fact]
        public void WillThrottleCalls() {
            var locker = GetLockProvider();
            if (locker == null)
                return;

            // sleep until start of throttling period
            Thread.Sleep(DateTime.Now.Ceiling(_period) - DateTime.Now);
            var sw = new Stopwatch();
            sw.Start();
            for (int i = 0; i < 5; i++)
                locker.AcquireLock("test");
            sw.Stop();

            _output.WriteLine(sw.Elapsed.ToString());
            Assert.True(sw.Elapsed.TotalSeconds < 1);

            sw.Reset();
            sw.Start();
            var result = locker.TryAcquireLock("test", acquireTimeout: TimeSpan.FromMilliseconds(250));
            sw.Stop();
            Assert.Null(result);
            _output.WriteLine(sw.Elapsed.ToString());

            sw.Reset();
            sw.Start();
            result = locker.TryAcquireLock("test", acquireTimeout: TimeSpan.FromSeconds(4));
            sw.Stop();
            Assert.NotNull(result);
            _output.WriteLine(sw.Elapsed.ToString());
        }

        public RedisThrottlingLockTests(CaptureFixture fixture, ITestOutputHelper output) : base(fixture, output) { }
    }
}