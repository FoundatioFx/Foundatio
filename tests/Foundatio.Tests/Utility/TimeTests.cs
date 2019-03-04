using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Foundatio.Logging.Xunit;
using Foundatio.Utility;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Utility {
    public class TimeTests : TestWithLoggingBase {
        public TimeTests(ITestOutputHelper output) : base(output) {}

        [Fact]
        public void CanGetTime() {
            using (var time = Time.UseTestTime()) {
                var now = DateTime.UtcNow;
                time.SetFrozenTime(now);
                Assert.Equal(now, Time.UtcNow);
                Assert.Equal(now.ToLocalTime(), Time.Now);
                Assert.Equal(now, Time.OffsetUtcNow);
                Assert.Equal(now.ToLocalTime(), Time.OffsetNow);
                Assert.Equal(DateTimeOffset.Now.Offset, Time.TimeZoneOffset);
            }
        }

        [Fact]
        public void CanSleep() {
            using (var time = Time.UseTestTime()) {
                var sw = Stopwatch.StartNew();
                Time.Delay(250);
                sw.Stop();
                Assert.InRange(sw.ElapsedMilliseconds, 225, 400);

                time.UseFakeSleep();

                var now = Time.UtcNow;
                sw.Restart();
                Time.Delay(1000);
                sw.Stop();
                var afterSleepNow = Time.UtcNow;

                Assert.InRange(sw.ElapsedMilliseconds, 0, 30);
                Assert.True(afterSleepNow > now);
                Assert.InRange(afterSleepNow.Subtract(now).TotalMilliseconds, 950, 1100);
            }
        }

        [Fact]
        public async Task CanSleepAsync() {
            using (var time = Time.UseTestTime()) {
                var sw = Stopwatch.StartNew();
                await Time.DelayAsync(250);
                sw.Stop();

                Assert.InRange(sw.ElapsedMilliseconds, 225, 3000);

                time.UseFakeSleep();

                var now = Time.UtcNow;
                sw.Restart();
                await Time.DelayAsync(1000);
                sw.Stop();
                var afterSleepNow = Time.UtcNow;

                Assert.InRange(sw.ElapsedMilliseconds, 0, 30);
                Assert.True(afterSleepNow > now);
                Assert.InRange(afterSleepNow.Subtract(now).TotalMilliseconds, 950, 5000);
            }
        }

        [Fact]
        public void CanSetTimeZone() {
            using (var time = Time.UseTestTime()) {
                var utcNow = DateTime.UtcNow;
                var now = new DateTime(utcNow.AddHours(1).Ticks, DateTimeKind.Local);
                time.SetFrozenTime(utcNow);
                time.SetTimeZoneOffset(TimeSpan.FromHours(1));

                Assert.Equal(utcNow, Time.UtcNow);
                Assert.Equal(utcNow, Time.OffsetUtcNow);
                Assert.Equal(now, Time.Now);
                Assert.Equal(new DateTimeOffset(now.Ticks, TimeSpan.FromHours(1)), Time.OffsetNow);
                Assert.Equal(TimeSpan.FromHours(1), Time.TimeZoneOffset);
            }
        }

        [Fact]
        public void CanSetLocalFixedTime() {
            using (var time = Time.UseTestTime()) {
                var now = DateTime.Now;
                var utcNow = now.ToUniversalTime();
                time.SetFrozenTime(now);

                Assert.Equal(now, Time.Now);
                Assert.Equal(now, Time.OffsetNow);
                Assert.Equal(utcNow, Time.UtcNow);
                Assert.Equal(utcNow, Time.OffsetUtcNow);
                Assert.Equal(DateTimeOffset.Now.Offset, Time.TimeZoneOffset);
            }
        }

        [Fact]
        public void CanSetUtcFixedTime() {
            using (var time = Time.UseTestTime()) {
                var utcNow = DateTime.UtcNow;
                var now = utcNow.ToLocalTime();
                time.SetFrozenTime(utcNow);

                Assert.Equal(now, Time.Now);
                Assert.Equal(now, Time.OffsetNow);
                Assert.Equal(utcNow, Time.UtcNow);
                Assert.Equal(utcNow, Time.OffsetUtcNow);
                Assert.Equal(DateTimeOffset.Now.Offset, Time.TimeZoneOffset);
            }
        }
    }
}
