using System;
using System.Diagnostics;
using Foundatio.Caching;
using Foundatio.Extensions;
using Foundatio.Lock;
using Foundatio.Utility;
using Xunit;

namespace Foundatio.Tests {
    public class ThrottlingLockTests : LockTestBase {
        protected override ILockProvider GetLockProvider() {
            return new ThrottlingLockProvider(new InMemoryCacheClient(), 5, TimeSpan.FromSeconds(1));
        }

        [Fact]
        public void WillThrottleCalls() {
            var locker = GetLockProvider();
            if (locker == null)
                return;

            // wait until we are at the beginning of our time bucket
            Run.UntilTrue(() => DateTime.UtcNow.Subtract(DateTime.UtcNow.Floor(TimeSpan.FromSeconds(1))).TotalMilliseconds < 100, null, TimeSpan.FromMilliseconds(50));

            var sw = new Stopwatch();
            sw.Start();
            for (int i = 0; i < 5; i++)
                locker.AcquireLock("test");
            sw.Stop();

            Assert.True(sw.Elapsed.TotalSeconds < 1);

            sw.Reset();
            sw.Start();
            locker.AcquireLock("test");
            sw.Stop();

            Trace.WriteLine(sw.Elapsed);
            Assert.InRange(sw.Elapsed.TotalSeconds, .8, 1.2);
        }
    }
}