using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Foundatio.Xunit;
using Foundatio.Utility;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Utility {
    public class SystemClockTests : TestWithLoggingBase {
        public SystemClockTests(ITestOutputHelper output) : base(output) {}

        [Fact]
        public void CanSetTime() {
            using (var clock = TestSystemClock.Install()) {
                var now = DateTime.Now;
                clock.SetTime(now);
                Assert.Equal(now, clock.Now);
                Assert.Equal(DateTimeOffset.Now.Offset, clock.Offset);
                Assert.Equal(now.ToUniversalTime(), clock.UtcNow);
                Assert.Equal(now.ToLocalTime(), clock.Now);
                Assert.Equal(now.ToUniversalTime(), clock.OffsetUtcNow);
                
                // set using utc
                now = DateTime.UtcNow;
                clock.SetTime(now);
                Assert.Equal(now, clock.UtcNow);
                Assert.Equal(DateTimeOffset.Now.Offset, clock.Offset);
                Assert.Equal(now.ToUniversalTime(), clock.UtcNow);
                Assert.Equal(now.ToLocalTime(), clock.Now);
                Assert.Equal(now.ToUniversalTime(), clock.OffsetUtcNow);
            }
        }

        [Fact]
        public void CanSetTimeWithOffset() {
            using (var clock = TestSystemClock.Install()) {
                var now = DateTimeOffset.Now;
                clock.SetTime(now.LocalDateTime, now.Offset);
                Assert.Equal(now, clock.OffsetNow);
                Assert.Equal(now.Offset, clock.Offset);
                Assert.Equal(now.UtcDateTime, clock.UtcNow);
                Assert.Equal(now.DateTime, clock.Now);
                Assert.Equal(now.ToUniversalTime(), clock.OffsetUtcNow);
                
                clock.SetTime(now.UtcDateTime, now.Offset);
                Assert.Equal(now, clock.OffsetNow);
                Assert.Equal(now.Offset, clock.Offset);
                Assert.Equal(now.UtcDateTime, clock.UtcNow);
                Assert.Equal(now.DateTime, clock.Now);
                Assert.Equal(now.ToUniversalTime(), clock.OffsetUtcNow);
                
                now = new DateTimeOffset(now.DateTime, TimeSpan.FromHours(1));
                clock.SetTime(now.LocalDateTime, now.Offset);
                Assert.Equal(now, clock.OffsetNow);
                Assert.Equal(now.Offset, clock.Offset);
                Assert.Equal(now.UtcDateTime, clock.UtcNow);
                Assert.Equal(now.DateTime, clock.Now);
                Assert.Equal(now.ToUniversalTime(), clock.OffsetUtcNow);
            }
        }

        [Fact]
        public void CanRealSleep() {
            var clock = new RealSystemClock(Log);
            var sw = Stopwatch.StartNew();
            clock.Sleep(250);
            sw.Stop();
            Assert.InRange(sw.ElapsedMilliseconds, 100, 500);
        }

        [Fact]
        public void CanTestSleep() {
            using (var clock = TestSystemClock.Install(Log)) {
                var startTime = clock.UtcNow;
                clock.Sleep(250);
                Assert.Equal(250, clock.UtcNow.Subtract(startTime).TotalMilliseconds);
            }
        }
        [Fact]
        public async Task CanRealSleepAsync() {
            var clock = new RealSystemClock(Log);
            var sw = Stopwatch.StartNew();
            await clock.SleepAsync(250);
            sw.Stop();
            Assert.InRange(sw.ElapsedMilliseconds, 100, 500);
        }

        [Fact]
        public async Task CanTestSleepAsync() {
            using (var clock = TestSystemClock.Install(Log)) {
                var startTime = clock.UtcNow;
                await clock.SleepAsync(250);
                Assert.Equal(250, clock.UtcNow.Subtract(startTime).TotalMilliseconds);
            }
        }
    }
}
