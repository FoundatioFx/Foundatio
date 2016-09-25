using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
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
            Log.MinimumLevel = LogLevel.Trace;
        }

        [Fact]
        public async Task CanRun() {
            var countdown = new AsyncCountdownEvent(1);
            Func<Task<DateTime?>> callback = () => {
                countdown.Signal();
                return null;
            };

            using (var timer = new ScheduledTimer(callback, loggerFactory: Log)) {
                timer.ScheduleNext();
                await countdown.WaitAsync(TimeSpan.FromMilliseconds(100));
                Assert.Equal(0, countdown.CurrentCount);
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
            var countdown = new AsyncCountdownEvent(2);

            Func<Task<DateTime?>> callback = async () => {
                _logger.Info("Starting work.");
                countdown.Signal();
                await SystemClock.SleepAsync(500);
                _logger.Info("Finished work.");
                return null;
            };

            using (var timer = new ScheduledTimer(callback, minimumIntervalTime: TimeSpan.FromMilliseconds(100), loggerFactory: Log)) {
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
        public async Task Test() {
            var countdown = new AsyncCountdownEvent(100);

            Action callback = () => {
                _logger.Info($"Working Thread:{Thread.CurrentThread.ManagedThreadId}");
                countdown.Signal();
            };

            var subject = new Subject<int>();
            subject
              .Buffer(TimeSpan.FromMilliseconds(10))
              //.Throttle(TimeSpan.FromMilliseconds(10))
              .Subscribe(i => callback());

            Parallel.For(0, 100, i => {
                _logger.Info($"Triggering Thread:{Thread.CurrentThread.ManagedThreadId}");
                subject.OnNext(0);
            });
            await countdown.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, countdown.CurrentCount);
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
    }
}