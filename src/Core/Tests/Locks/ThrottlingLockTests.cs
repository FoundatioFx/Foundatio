using System;
using System.Diagnostics;
using Foundatio.Caching;
using Foundatio.Extensions;
using Foundatio.Lock;
using Foundatio.Utility;
using Xunit;

namespace Foundatio.Tests {
    public class ThrottlingLockTests : LockTestBase {
        private readonly TimeSpan _period = TimeSpan.FromSeconds(2);

        protected override ILockProvider GetLockProvider() {
            return new ThrottlingLockProvider(new InMemoryCacheClient(), 5, _period);
        }

        [Fact]
        public void WillThrottleCalls() {
            var locker = GetLockProvider();
            if (locker == null)
                return;

            Trace.WriteLine(DateTime.UtcNow.Subtract(DateTime.UtcNow.Floor(_period)).TotalMilliseconds);
            Trace.WriteLine(DateTime.UtcNow.ToString("mm:ss.fff"));
            // wait until we are at the beginning of our time bucket
            Run.UntilTrue(() => DateTime.UtcNow.Subtract(DateTime.UtcNow.Floor(_period)).TotalMilliseconds < 100, null, TimeSpan.FromMilliseconds(50));
            Trace.WriteLine(DateTime.UtcNow.Subtract(DateTime.UtcNow.Floor(_period)).TotalMilliseconds);
            Trace.WriteLine(DateTime.UtcNow.ToString("mm:ss.fff"));

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
            Assert.InRange(sw.Elapsed.TotalSeconds, 1.2, 2.2);
        }
    }
}