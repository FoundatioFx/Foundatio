using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Logging;
using Foundatio.Logging.Xunit;
using Foundatio.Tests.Extensions;
using Foundatio.Utility;
using Nito.AsyncEx;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Utility {
    public class ScheduledTimerTests : TestWithLoggingBase {
        public ScheduledTimerTests(ITestOutputHelper output) : base(output) {
            Log.SetLogLevel<ScheduledTimer>(LogLevel.Trace);
            SystemClock.Reset();
        }

        [Fact]
        public async Task CanRun() {
            int hits = 0;
            Func<Task<DateTime?>> callback = async () => {
                Interlocked.Increment(ref hits);
                await SystemClock.SleepAsync(50);
                return null;
            };

            using (var timer = new ScheduledTimer(callback, loggerFactory: Log)) {
                timer.ScheduleNext();
                await SystemClock.SleepAsync(50);
                Assert.Equal(1, hits);
            }
        }

        [Fact]
        public async Task CanRunAndScheduleConcurrently() {
            var countdown = new AsyncCountdownEvent(2);
            
            Func<Task<DateTime?>> callback = async () => {
                _logger.Info("Starting work.");
                countdown.Signal();
                await SystemClock.SleepAsync(500);
                _logger.Info("Finished work.");
                return null;
            };

            using (var timer = new ScheduledTimer(callback, loggerFactory: Log)) {
                for (int i = 0; i < 4; i++) {
                    timer.ScheduleNext();
                    SystemClock.Sleep(1);
                }

                await countdown.WaitAsync(TimeSpan.FromMilliseconds(100));
                Assert.Equal(1, countdown.CurrentCount);
                
                await countdown.WaitAsync(TimeSpan.FromSeconds(1.5));
                Assert.Equal(0, countdown.CurrentCount);
            }
        }

        [Fact]
        public async Task CanRunWithMinimumInterval() {
            var resetEvent = new AsyncAutoResetEvent(false);
            
            int hits = 0;
            Func<Task<DateTime?>> callback = () => {
                Interlocked.Increment(ref hits);
                _logger.Info($"hits: {hits}");
                resetEvent.Set();
                return Task.FromResult<DateTime?>(null);
            };
            
            using (var timer = new ScheduledTimer(callback, minimumIntervalTime: TimeSpan.FromMilliseconds(100), loggerFactory: Log)) {
                var sw = Stopwatch.StartNew();
                timer.ScheduleNext();
                await SystemClock.SleepAsync(1);
                timer.ScheduleNext();
                await SystemClock.SleepAsync(1);
                timer.ScheduleNext();

                await resetEvent.WaitAsync(new CancellationTokenSource(100).Token);
                Assert.InRange(hits, 1, 2);
                
                await resetEvent.WaitAsync(new CancellationTokenSource(2000).Token);
                sw.Stop();

                Assert.InRange(hits, 2, 3);
                Assert.InRange(sw.ElapsedMilliseconds, 100, 2000);
            }
        }
        
        [Fact]
        public async Task CanRecoverFromError() {
            var resetEvent = new AsyncAutoResetEvent(false);

            int hits = 0;
            Func<Task<DateTime?>> callback = () => {
                Interlocked.Increment(ref hits);
                _logger.Info("Callback called for the #{time} time", hits);
                if (hits == 1)
                    throw new Exception("Error in callback");

                resetEvent.Set();
                return Task.FromResult<DateTime?>(null);
            };

            using (var timer = new ScheduledTimer(callback, loggerFactory: Log)) {
                timer.ScheduleNext();

                await resetEvent.WaitAsync(new CancellationTokenSource(500).Token);
                Assert.Equal(2, hits);
            }
        }

        [Fact]
        public async Task CanRunConcurrent() {
            int hits = 0;
            Func<Task<DateTime?>> callback = () => {
                int i = Interlocked.Increment(ref hits);
                _logger.Info($"Running {i}...");
                return Task.FromResult<DateTime?>(null);
            };

            using (var timer = new ScheduledTimer(callback, minimumIntervalTime: TimeSpan.FromMilliseconds(100), loggerFactory: Log)) {
                for (int i = 1; i <= 5; i++) {
                    _logger.Info($"Scheduling #{i}");
                    timer.ScheduleNext();
                    await SystemClock.SleepAsync(5);
                }

                await SystemClock.SleepAsync(250);
                Assert.Equal(2, hits);

                await SystemClock.SleepAsync(100);
                Assert.Equal(2, hits);
            }
        }
    }
}
