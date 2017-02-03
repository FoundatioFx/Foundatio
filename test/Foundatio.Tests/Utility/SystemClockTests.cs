using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Foundatio.Logging.Xunit;
using Foundatio.Utility;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Utility {
    public class SystemClockTests : TestWithLoggingBase {
        public SystemClockTests(ITestOutputHelper output) : base(output) {}

        [Fact]
        public void CanGetTime() {
            using (TestSystemClock.Install()) {
                var now = DateTime.UtcNow;
                SystemClock.Test.SetFixedTime(now);
                Assert.Equal(now, SystemClock.UtcNow);
                Assert.Equal(now.ToLocalTime(), SystemClock.Now);
                Assert.Equal(now, SystemClock.OffsetUtcNow);
                Assert.Equal(now.ToLocalTime(), SystemClock.OffsetNow);
                Assert.Equal(DateTimeOffset.Now.Offset, SystemClock.TimeZoneOffset);
            }
        }

        [Fact]
        public void CanSleep() {
            using (TestSystemClock.Install()) {
                var sw = Stopwatch.StartNew();
                SystemClock.Sleep(250);
                sw.Stop();
                Assert.InRange(sw.ElapsedMilliseconds, 225, 400);

                SystemClock.Test.UseFakeSleep();

                var now = SystemClock.UtcNow;
                sw.Restart();
                SystemClock.Sleep(1000);
                sw.Stop();
                var afterSleepNow = SystemClock.UtcNow;

                Assert.InRange(sw.ElapsedMilliseconds, 0, 30);
                Assert.True(afterSleepNow > now);
                Assert.InRange(afterSleepNow.Subtract(now).TotalMilliseconds, 950, 1100);
            }
        }

        [Fact]
        public async Task CanSleepAsync() {
            using (TestSystemClock.Install()) {
                var sw = Stopwatch.StartNew();
                await SystemClock.SleepAsync(250);
                sw.Stop();

                Assert.InRange(sw.ElapsedMilliseconds, 225, 400);

                SystemClock.Test.UseFakeSleep();

                var now = SystemClock.UtcNow;
                sw.Restart();
                await SystemClock.SleepAsync(1000);
                sw.Stop();
                var afterSleepNow = SystemClock.UtcNow;

                Assert.InRange(sw.ElapsedMilliseconds, 0, 30);
                Assert.True(afterSleepNow > now);
                Assert.InRange(afterSleepNow.Subtract(now).TotalMilliseconds, 950, 1100);
            }
        }

        [Fact]
        public void CanSetTimeZone() {
            using (TestSystemClock.Install()) {
                var utcNow = DateTime.UtcNow;
                var now = new DateTime(utcNow.AddHours(1).Ticks, DateTimeKind.Local);
                SystemClock.Test.SetFixedTime(utcNow);
                SystemClock.Test.SetTimeZoneOffset(TimeSpan.FromHours(1));

                Assert.Equal(utcNow, SystemClock.UtcNow);
                Assert.Equal(utcNow, SystemClock.OffsetUtcNow);
                Assert.Equal(now, SystemClock.Now);
                Assert.Equal(new DateTimeOffset(now.Ticks, TimeSpan.FromHours(1)), SystemClock.OffsetNow);
                Assert.Equal(TimeSpan.FromHours(1), SystemClock.TimeZoneOffset);
            }
        }

        [Fact]
        public void CanSetLocalFixedTime() {
            using (TestSystemClock.Install()) {
                var now = DateTime.Now;
                var utcNow = now.ToUniversalTime();
                SystemClock.Test.SetFixedTime(now);

                Assert.Equal(now, SystemClock.Now);
                Assert.Equal(now, SystemClock.OffsetNow);
                Assert.Equal(utcNow, SystemClock.UtcNow);
                Assert.Equal(utcNow, SystemClock.OffsetUtcNow);
                Assert.Equal(DateTimeOffset.Now.Offset, SystemClock.TimeZoneOffset);
            }
        }

        [Fact]
        public void CanSetUtcFixedTime() {
            using (TestSystemClock.Install()) {
                var utcNow = DateTime.UtcNow;
                var now = utcNow.ToLocalTime();
                SystemClock.Test.SetFixedTime(utcNow);

                Assert.Equal(now, SystemClock.Now);
                Assert.Equal(now, SystemClock.OffsetNow);
                Assert.Equal(utcNow, SystemClock.UtcNow);
                Assert.Equal(utcNow, SystemClock.OffsetUtcNow);
                Assert.Equal(DateTimeOffset.Now.Offset, SystemClock.TimeZoneOffset);
            }
        }
    }
}
