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
            using (var clock = TestSystemClock.Install()) {
                var now = DateTime.UtcNow;
                clock.SetTime(now);
                Assert.Equal(now, SystemClock.UtcNow);
                Assert.Equal(now.ToLocalTime(), SystemClock.Now);
                Assert.Equal(now, SystemClock.OffsetUtcNow);
                Assert.Equal(now.ToLocalTime(), SystemClock.OffsetNow);
                Assert.Equal(DateTimeOffset.Now.Offset, SystemClock.TimeZoneOffset);
            }
        }

        [Fact]
        public void CanSleep() {
            using (var clock = TestSystemClock.Install()) {
                var sw = Stopwatch.StartNew();
                SystemClock.Sleep(250);
                sw.Stop();
                Assert.InRange(sw.ElapsedMilliseconds, 225, 400);

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
            using (var clock = TestSystemClock.Install()) {
                var sw = Stopwatch.StartNew();
                await SystemClock.SleepAsync(250);
                sw.Stop();

                Assert.InRange(sw.ElapsedMilliseconds, 225, 3000);

                var now = SystemClock.UtcNow;
                sw.Restart();
                await SystemClock.SleepAsync(1000);
                sw.Stop();
                var afterSleepNow = SystemClock.UtcNow;

                Assert.InRange(sw.ElapsedMilliseconds, 0, 30);
                Assert.True(afterSleepNow > now);
                Assert.InRange(afterSleepNow.Subtract(now).TotalMilliseconds, 950, 5000);
            }
        }

        [Fact]
        public void CanSetTimeZone() {
            using (var clock = TestSystemClock.Install()) {
                var utcNow = DateTime.UtcNow;
                var now = new DateTime(utcNow.AddHours(1).Ticks, DateTimeKind.Local);
                clock.SetTime(utcNow);

                Assert.Equal(utcNow, SystemClock.UtcNow);
                Assert.Equal(utcNow, SystemClock.OffsetUtcNow);
                Assert.Equal(now, SystemClock.Now);
                Assert.Equal(new DateTimeOffset(now.Ticks, TimeSpan.FromHours(1)), SystemClock.OffsetNow);
                Assert.Equal(TimeSpan.FromHours(1), SystemClock.TimeZoneOffset);
            }
        }

        [Fact]
        public void CanSetLocalFixedTime() {
            using (var clock = TestSystemClock.Install()) {
                var now = DateTime.Now;
                var utcNow = now.ToUniversalTime();
                clock.SetTime(now);

                Assert.Equal(now, SystemClock.Now);
                Assert.Equal(now, SystemClock.OffsetNow);
                Assert.Equal(utcNow, SystemClock.UtcNow);
                Assert.Equal(utcNow, SystemClock.OffsetUtcNow);
                Assert.Equal(DateTimeOffset.Now.Offset, SystemClock.TimeZoneOffset);
            }
        }

        [Fact]
        public void CanSetUtcFixedTime() {
            using (var clock = TestSystemClock.Install()) {
                var utcNow = DateTime.UtcNow;
                var now = utcNow.ToLocalTime();
                clock.SetTime(utcNow);

                Assert.Equal(now, SystemClock.Now);
                Assert.Equal(now, SystemClock.OffsetNow);
                Assert.Equal(utcNow, SystemClock.UtcNow);
                Assert.Equal(utcNow, SystemClock.OffsetUtcNow);
                Assert.Equal(DateTimeOffset.Now.Offset, SystemClock.TimeZoneOffset);
            }
        }
    }
}
