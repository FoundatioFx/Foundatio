﻿using System;
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
            Log.SetLogLevel<ScheduledTimer>(LogLevel.Trace);

            int hits = 0;
            Func<Task<DateTime?>> callback = async () => {
                _logger.Info("Starting work.");
                Interlocked.Increment(ref hits);
                await SystemClock.SleepAsync(1000);
                _logger.Info("Finished work.");
                return null;
            };

            using (var timer = new ScheduledTimer(callback, loggerFactory: Log)) {
                timer.ScheduleNext();
                await SystemClock.SleepAsync(1);
                timer.ScheduleNext();

                await SystemClock.SleepAsync(50);
                Assert.Equal(1, hits);

                await SystemClock.SleepAsync(1000);
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
                var sw = Stopwatch.StartNew();
                timer.ScheduleNext();
                await SystemClock.SleepAsync(1);
                timer.ScheduleNext();
                await SystemClock.SleepAsync(1);
                timer.ScheduleNext();

                await resetEvent.WaitAsync(new CancellationTokenSource(100).Token);
                Assert.Equal(1, hits);
                
                await resetEvent.WaitAsync(new CancellationTokenSource(2000).Token);
                sw.Stop();

                Assert.Equal(2, hits);
                Assert.InRange(sw.ElapsedMilliseconds, 100, 2000);
            }
        }
        
        [Fact]
        public async Task CanRecoverFromError() {
            Log.SetLogLevel<ScheduledTimer>(LogLevel.Trace);
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
            Log.SetLogLevel<ScheduledTimer>(LogLevel.Trace);

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
