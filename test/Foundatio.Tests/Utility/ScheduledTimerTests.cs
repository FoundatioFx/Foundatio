using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Logging.Xunit;
using Foundatio.Tests.Extensions;
using Foundatio.Utility;
using Foundatio.AsyncEx;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Utility {
    public class ScheduledTimerTests : TestWithLoggingBase {
        public ScheduledTimerTests(ITestOutputHelper output) : base(output) {
            Log.SetLogLevel<ScheduledTimer>(LogLevel.Trace);
        }

        [Fact]
        public async Task CanRun() {
            var resetEvent = new AsyncAutoResetEvent();
            Func<Task<DateTime?>> callback = () => {
                resetEvent.Set();
                return null;
            };

            using (var timer = new ScheduledTimer(callback, loggerFactory: Log)) {
                timer.ScheduleNext();
                await resetEvent.WaitAsync(new CancellationTokenSource(500).Token);
            }
        }

        [Fact]
        public Task CanRunAndScheduleConcurrently() {
            return CanRunConcurrentlyAsync();
        }

        [Fact]
        public Task CanRunWithMinimumInterval() {
            return CanRunConcurrentlyAsync(TimeSpan.FromMilliseconds(100));
        }

        private async Task CanRunConcurrentlyAsync(TimeSpan? minimumIntervalTime = null) {
            var countdown = new AsyncCountdownEvent(2);

            Func<Task<DateTime?>> callback = async () => {
                _logger.LogInformation("Starting work.");
                countdown.Signal();
                await SystemClock.SleepAsync(500);
                _logger.LogInformation("Finished work.");
                return null;
            };

            using (var timer = new ScheduledTimer(callback, minimumIntervalTime: minimumIntervalTime, loggerFactory: Log)) {
                timer.ScheduleNext();
                var t = Task.Run(async () => {
                    for (int i = 0; i < 3; i++) {
                        await SystemClock.SleepAsync(10);
                        timer.ScheduleNext();
                    }
                });

                _logger.LogInformation("Waiting for 300ms");
                await countdown.WaitAsync(TimeSpan.FromMilliseconds(300));
                _logger.LogInformation("Finished waiting for 300ms");
                Assert.Equal(1, countdown.CurrentCount);

                _logger.LogInformation("Waiting for 1.5 seconds");
                await countdown.WaitAsync(TimeSpan.FromSeconds(1.5));
                _logger.LogInformation("Finished waiting for 1.5 seconds");
                Assert.Equal(0, countdown.CurrentCount);
            }
        }

        [Fact]
        public async Task CanRecoverFromError() {
            var resetEvent = new AsyncAutoResetEvent(false);

            int hits = 0;
            Func<Task<DateTime?>> callback = () => {
                Interlocked.Increment(ref hits);
                if (_logger.IsEnabled(LogLevel.Information)) _logger.LogInformation("Callback called for the #{Hits} time", hits);
                if (hits == 1)
                    throw new Exception("Error in callback");

                resetEvent.Set();
                return Task.FromResult<DateTime?>(null);
            };

            using (var timer = new ScheduledTimer(callback, loggerFactory: Log)) {
                timer.ScheduleNext();
                await resetEvent.WaitAsync(new CancellationTokenSource(800).Token);
                Assert.Equal(2, hits);
            }
        }
    }
}