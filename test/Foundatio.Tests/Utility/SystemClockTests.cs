using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Foundatio.Logging.Xunit;
using Foundatio.Utility;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Utility
{
    public class SystemClockTests : TestWithLoggingBase
    {
        public SystemClockTests(ITestOutputHelper output) : base(output)
        {
        }

        [Fact]
        public async Task CanSleep()
        {
            var sw = Stopwatch.StartNew();
            SystemClock.Sleep(250);
            sw.Stop();

            Assert.InRange(sw.ElapsedMilliseconds, 225, 400);

            using (TestSystemClock.Install()) {
                var now = SystemClock.UtcNow;
                sw.Restart();
                var task = Task.Run(() => SystemClock.Sleep(1000));
                TestSystemClock.AdvanceBy(TimeSpan.FromMilliseconds(1000));
                await task;
                sw.Stop();
                var afterSleepNow = SystemClock.UtcNow;

                Assert.InRange(sw.ElapsedMilliseconds, 0, 25);
                Assert.True(afterSleepNow > now);
                Assert.InRange(afterSleepNow.Subtract(now).TotalMilliseconds, 950, 1100);
            }
        }

        [Fact]
        public async Task CanSleepAsync()
        {
            var sw = Stopwatch.StartNew();
            await SystemClock.SleepAsync(250);
            sw.Stop();

            Assert.InRange(sw.ElapsedMilliseconds, 225, 400);

            using (TestSystemClock.Install()) {
                var now = SystemClock.UtcNow;
                sw.Restart();
                var task = SystemClock.SleepAsync(1000);
                TestSystemClock.AdvanceBy(TimeSpan.FromMilliseconds(1000));
                await task;
                sw.Stop();
                var afterSleepNow = SystemClock.UtcNow;

                Assert.InRange(sw.ElapsedMilliseconds, 0, 25);
                Assert.True(afterSleepNow > now);
                Assert.InRange(afterSleepNow.Subtract(now).TotalMilliseconds, 950, 1100);
            }
        }
    }
}
