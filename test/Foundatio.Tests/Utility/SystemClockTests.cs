using System;
using System.Diagnostics;
using System.Reactive.Concurrency;
using System.Threading.Tasks;
using Foundatio.Logging.Xunit;
using Foundatio.Utility;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Utility {
    public class SystemClockTests : TestWithLoggingBase {
        public SystemClockTests(ITestOutputHelper output) : base(output) {
            TestSystemClock.Install();
        }

        [Fact]
        public void CanGetTime() {
            Assert.Equal(new DateTimeOffset(0, TimeSpan.Zero), SystemClock.UtcNow);
        }

        [Fact]
        public async Task CanSleepAsync()
        {
            var sw = Stopwatch.StartNew();

            var now = SystemClock.UtcNow;
            var task = SystemClock.SleepAsync(1000);
            TestSystemClock.Instance.Scheduler.AdvanceTo(999);
            Assert.False(task.IsCompleted);
            TestSystemClock.Instance.Scheduler.AdvanceTo(1000);
            await task;

            var afterSleepNow = SystemClock.UtcNow;

            Assert.Equal(0, now.Ticks);
            Assert.Equal(1000, afterSleepNow.Ticks);
            Assert.InRange(sw.ElapsedMilliseconds, 0, 500);
        }
    }
}
