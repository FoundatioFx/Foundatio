using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Logging;
using Foundatio.Logging.Xunit;
using Foundatio.Queues;
using Foundatio.Utility;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Utility {
    public class ScheduledTimerTests : TestWithLoggingBase {
        public ScheduledTimerTests(ITestOutputHelper output) : base(output) { }

        [Fact]
        public async Task CanRun() {
            Log.SetLogLevel<ScheduledTimer>(LogLevel.Trace);

            int hits = 0;
            var timer = new ScheduledTimer(async () => {
                Interlocked.Increment(ref hits);
                await Task.Delay(50);
                return null;
            }, loggerFactory: Log);

            timer.Run();

            await Task.Delay(1);
            Assert.Equal(1, hits);
        }

        [Fact]
        public async Task CanRunWithMinimumInterval() {
            Log.SetLogLevel<ScheduledTimer>(LogLevel.Trace);

            int hits = 0;
            var timer = new ScheduledTimer(async () => {
                Interlocked.Increment(ref hits);
                await Task.Delay(50);
                return null;
            }, minimumIntervalTime: TimeSpan.FromMilliseconds(50), loggerFactory: Log);

            timer.Run();
            timer.Run();

            await Task.Delay(1);
            Assert.Equal(1, hits);

            await Task.Delay(110);
            Assert.Equal(2, hits);
        }

        [Fact]
        public async Task CanRunConcurrent() {
            int hits = 0;
            Log.SetLogLevel<ScheduledTimer>(LogLevel.Trace);

            var timer = new ScheduledTimer(() => {
                int i = Interlocked.Increment(ref hits);
                _logger.Info($"Running {i}...");
                return Task.FromResult<DateTime?>(null);
            }, minimumIntervalTime: TimeSpan.FromMilliseconds(250), loggerFactory: Log);

            for (int i = 0; i < 5; i++) {
                timer.Run();
                await Task.Delay(5);
            }

            await Task.Delay(1000);

            Assert.Equal(2, hits);

            await Task.Delay(100);
            Assert.Equal(2, hits);
        }
    }
}
