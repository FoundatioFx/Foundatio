using System;
using System.Diagnostics;
using System.Threading;
using Foundatio.Caching;
using Foundatio.Extensions;
using Foundatio.Lock;
using Foundatio.Logging;
using Foundatio.Tests.Utility;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests {
    public class ThrottlingLockTests : LockTestBase {
        private readonly TimeSpan _period = TimeSpan.FromSeconds(1);

        public ThrottlingLockTests(CaptureFixture fixture, ITestOutputHelper output)
            : base(fixture, output)
        {
            MinimumLogLevel = LogLevel.Warn;
        }

        protected override ILockProvider GetLockProvider() {
            return new ThrottlingLockProvider(new InMemoryCacheClient(), 5, _period);
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
            var result = locker.AcquireLock("test", acquireTimeout: TimeSpan.Zero);
            sw.Stop();
            Assert.Null(result);
            _output.WriteLine(sw.Elapsed.ToString());

            sw.Reset();
            sw.Start();
            result = locker.AcquireLock("test", acquireTimeout: TimeSpan.FromMilliseconds(250));
            sw.Stop();
            Assert.Null(result);
            _output.WriteLine(sw.Elapsed.ToString());

            sw.Reset();
            sw.Start();
            result = locker.AcquireLock("test", acquireTimeout: TimeSpan.FromSeconds(2));
            sw.Stop();
            Assert.NotNull(result);
            _output.WriteLine(sw.Elapsed.ToString());
        }
    }
}