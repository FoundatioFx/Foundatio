using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Logging;
using Foundatio.Logging.Xunit;
using Foundatio.Utility;
using Nito.AsyncEx;
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

            timer.ScheduleNext();

            await Task.Delay(50);
            Assert.Equal(1, hits);
        }

        [Fact]
        public async Task CanRunAndScheduleConcurrently() {
            Log.SetLogLevel<ScheduledTimer>(LogLevel.Trace);

            int hits = 0;
            var timer = new ScheduledTimer(async () => {
                _logger.Info("Starting work.");
                Interlocked.Increment(ref hits);
                await Task.Delay(1000);
                _logger.Info("Finished work.");
                return null;
            }, loggerFactory: Log);

            timer.ScheduleNext();
            await Task.Delay(1);
            timer.ScheduleNext();

            await Task.Delay(50);
            Assert.Equal(1, hits);

            await Task.Delay(1000);
            Assert.Equal(2, hits);
        }

        [Fact]
        public async Task CanRunWithMinimumInterval() {
            Log.SetLogLevel<ScheduledTimer>(LogLevel.Trace);
            var resetEvent = new AsyncAutoResetEvent(false);

            int hits = 0;
            var timer = new ScheduledTimer(() => {
                Interlocked.Increment(ref hits);
                resetEvent.Set();
                return Task.FromResult<DateTime?>(null);
            }, minimumIntervalTime: TimeSpan.FromMilliseconds(100), loggerFactory: Log);

            timer.ScheduleNext();
            await Task.Delay(1);
            timer.ScheduleNext();
            await Task.Delay(1);
            timer.ScheduleNext();

            await resetEvent.WaitAsync(new CancellationTokenSource(100).Token);
            var sw = Stopwatch.StartNew();
            Assert.Equal(1, hits);

            await resetEvent.WaitAsync(new CancellationTokenSource(500).Token);
            sw.Stop();
            Assert.Equal(2, hits);

            Assert.Throws<TaskCanceledException>(() => {
                resetEvent.Wait(new CancellationTokenSource(100).Token);
            });

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
                timer.ScheduleNext();
                await Task.Delay(5);
            }

            await Task.Delay(1000);

            Assert.Equal(2, hits);

            await Task.Delay(100);
            Assert.Equal(2, hits);
        }
    }
}
