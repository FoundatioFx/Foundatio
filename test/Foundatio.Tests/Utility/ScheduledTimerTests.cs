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
            Func<Task<DateTime?>> callback = async () => {
                Interlocked.Increment(ref hits);
                await Task.Delay(50);
                return null;
            };

            using (var timer = new ScheduledTimer(callback, loggerFactory: Log)) {
                timer.ScheduleNext();
                await Task.Delay(50);
                Assert.Equal(1, hits);
            }
        }

        [Fact]
        public async Task CanRunAndScheduleConcurrently() {
            Log.SetLogLevel<ScheduledTimer>(LogLevel.Trace);

            int hits = 0;
            Func<Task<DateTime?>> callback = async () => {
                _logger.Info("Starting work.");
                Interlocked.Increment(ref hits);
                await Task.Delay(1000);
                _logger.Info("Finished work.");
                return null;
            };

            using (var timer = new ScheduledTimer(callback, loggerFactory: Log)) {
                timer.ScheduleNext();
                await Task.Delay(1);
                timer.ScheduleNext();

                await Task.Delay(50);
                Assert.Equal(1, hits);

                await Task.Delay(1000);
                Assert.Equal(2, hits);
            }
        }

        [Fact]
        public async Task CanRunWithMinimumInterval() {
            Log.SetLogLevel<ScheduledTimer>(LogLevel.Trace);
            var resetEvent = new AsyncAutoResetEvent(false);

            int hits = 0;
            Func<Task<DateTime?>> callback = () => {
                Interlocked.Increment(ref hits);
                resetEvent.Set();
                return Task.FromResult<DateTime?>(null);
            };

            using (var timer = new ScheduledTimer(callback, minimumIntervalTime: TimeSpan.FromMilliseconds(100), loggerFactory: Log)) {
                timer.ScheduleNext();
                await Task.Delay(1);
                timer.ScheduleNext();
                await Task.Delay(1);
                timer.ScheduleNext();

                await resetEvent.WaitAsync(new CancellationTokenSource(100).Token);
                Assert.Equal(1, hits);

                await resetEvent.WaitAsync(new CancellationTokenSource(125).Token);
                Assert.Equal(2, hits);

                Assert.Throws<TaskCanceledException>(() => { resetEvent.Wait(new CancellationTokenSource(50).Token); });
                await Task.Delay(75);
                Assert.Equal(3, hits);
            }
        }

        [Fact]
        public async Task CanRunConcurrent() {
            Log.SetLogLevel<ScheduledTimer>(LogLevel.Trace);

            int hits = 0;
            Func<Task<DateTime?>> callback = () => {
                int i = Interlocked.Increment(ref hits);
                _logger.Info($"Running {i}...");
                return Task.FromResult<DateTime?>(null);
            };

            using (var timer = new ScheduledTimer(callback, minimumIntervalTime: TimeSpan.FromMilliseconds(100), loggerFactory: Log)) {
                for (int i = 0; i < 5; i++) {
                    _logger.Info($"Scheduling #{i}");
                    timer.ScheduleNext();
                    await Task.Delay(5);
                }

                await Task.Delay(250);
                Assert.Equal(2, hits);

                await Task.Delay(100);
                Assert.Equal(2, hits);
            }
        }
    }
}
