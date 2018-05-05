using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Attributes.Jobs;
using Foundatio.Logging.Xunit;
using Foundatio.Queues;
using Foundatio.TestHarness.Utility;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Queue {
    public class TaskQueueTests : TestWithLoggingBase {
        public TaskQueueTests(ITestOutputHelper output) : base(output) { }

        [Fact]
        public async Task CanProcessEmptyQueue() {
            Log.MinimumLevel = LogLevel.Trace;
            var queue = new TaskQueue(loggerFactory: Log);
            await SystemClock.SleepAsync(30);
            queue.Dispose();
            await SystemClock.SleepAsync(1);
            Assert.Equal(0, queue.Working);
        }

        [Fact]
        public void CanRun() {
            Log.MinimumLevel = LogLevel.Trace;

            int completed = 0;
            var countdown = new CountdownEvent(1);
            using(var queue = new TaskQueue(autoStart: false, queueEmptyAction: () => countdown.Signal(), loggerFactory: Log)) {
                queue.Enqueue(() => {
                    _logger.LogTrace("Running Task");
                    Interlocked.Increment(ref completed);
                    return Task.CompletedTask;
                });

                Assert.Equal(1, queue.Queued);
                queue.Start();

                countdown.Wait(TimeSpan.FromSeconds(2));
                Assert.Equal(0, countdown.CurrentCount);
                Assert.Equal(0, queue.Queued);
                Assert.Equal(1, completed);
            }
        }

        [Fact]
        public void CanRunAndWait() {
            Log.MinimumLevel = LogLevel.Trace;

            int completed = 0;
            var countdown = new CountdownEvent(1);
            using(var queue = new TaskQueue(autoStart: false, queueEmptyAction: () => countdown.Signal(), loggerFactory: Log)) {
                queue.Enqueue(() => {
                    _logger.LogTrace("Running Task");
                    Interlocked.Increment(ref completed);
                    return Task.CompletedTask;
                });

                Assert.Equal(1, queue.Queued);
                queue.Start();

                countdown.Wait(TimeSpan.FromSeconds(2));
                Assert.Equal(0, countdown.CurrentCount);
                Assert.Equal(0, queue.Queued);
                Assert.Equal(1, completed);

                SystemClock.Sleep(30);
                countdown.Reset();

                queue.Enqueue(() => {
                    _logger.LogTrace("Running Task");
                    Interlocked.Increment(ref completed);
                    return Task.CompletedTask;
                });

                countdown.Wait(TimeSpan.FromSeconds(2));
                Assert.Equal(0, countdown.CurrentCount);
                Assert.Equal(0, queue.Queued);
                Assert.Equal(2, completed);
            }
        }

        [Fact]
        public void CanRespectMaxItems() {
            Log.MinimumLevel = LogLevel.Trace;

            using (var queue = new TaskQueue(maxItems: 1, autoStart: false, loggerFactory: Log)) {
                queue.Enqueue(() => Task.CompletedTask);
                queue.Enqueue(() => Task.CompletedTask);

                Assert.Equal(1, queue.Queued);
                Assert.Equal(0, queue.Working);
            }
        }

        [Fact]
        public void CanHandleTaskFailure() {
            Log.MinimumLevel = LogLevel.Trace;

            int completed = 0;
            var countdown = new CountdownEvent(1);
            using(var queue = new TaskQueue(autoStart: false, queueEmptyAction: () => {
                if (completed > 0)
                    countdown.Signal();
            }, loggerFactory: Log)) {
                queue.Enqueue(() => {
                    _logger.LogTrace("Running Task 1");
                    throw new Exception("Exception in Queued Task");
                });

                queue.Enqueue(() => {
                    _logger.LogTrace("Running Task 2");
                    throw new Exception("Exception in Queued Task");
                });

                queue.Enqueue(() => {
                    _logger.LogTrace("Running Task 3");
                    Interlocked.Increment(ref completed);
                    return Task.CompletedTask;
                });

                Assert.Equal(3, queue.Queued);
                Assert.Equal(0, queue.Working);
                queue.Start();

                countdown.Wait(TimeSpan.FromSeconds(2));
                Assert.Equal(0, countdown.CurrentCount);
                Assert.Equal(0, queue.Queued);
                Assert.Equal(0, queue.Working);
                Assert.Equal(1, completed);
            }
        }

        [Fact]
        public void CanRunInParallel() {
            Log.MinimumLevel = LogLevel.Trace;

            var countdown = new CountdownEvent(1);
            using(var queue = new TaskQueue(maxDegreeOfParallelism: 2, autoStart: false, queueEmptyAction: () => countdown.Signal(), loggerFactory: Log)) {
                int completed = 0;
                queue.Enqueue(async () => { await SystemClock.SleepAsync(10); Interlocked.Increment(ref completed); });
                queue.Enqueue(async () => { await SystemClock.SleepAsync(50); Interlocked.Increment(ref completed); });
                queue.Enqueue(() => { Assert.Equal(2, queue.Working); Interlocked.Increment(ref completed); return Task.CompletedTask; });
                Assert.Equal(3, queue.Queued);
                Assert.Equal(0, queue.Working);

                queue.Start();

                countdown.Wait(TimeSpan.FromSeconds(2));
                Assert.Equal(0, countdown.CurrentCount);
                Assert.Equal(0, queue.Queued);
                Assert.Equal(0, queue.Working);
                Assert.Equal(3, completed);
            }
        }

        [Fact]
        public void CanRunContinuously() {
            Log.MinimumLevel = LogLevel.Trace;

            int completed = 0;
            var countdown = new CountdownEvent(1);
            using(var queue = new TaskQueue(queueEmptyAction: () => {
                if (completed > 2)
                    countdown.Signal();
            }, loggerFactory: Log)) {
                queue.Enqueue(() => { Interlocked.Increment(ref completed); return Task.CompletedTask; });
                queue.Enqueue(() => { Interlocked.Increment(ref completed); return Task.CompletedTask; });
                queue.Enqueue(() => { Interlocked.Increment(ref completed); return Task.CompletedTask; });
                Assert.InRange(queue.Queued, 1, 3);

                countdown.Wait(TimeSpan.FromSeconds(2));
                Assert.Equal(0, countdown.CurrentCount);
                Assert.Equal(0, queue.Queued);
                Assert.Equal(0, queue.Working);
                Assert.Equal(3, completed);
            }
        }

        [Fact]
        public void CanProcessQuickly() {
            Log.MinimumLevel = LogLevel.Debug;
            const int NumberOfEnqueuedItems = 1000;

            int completed = 0;
            var countdown = new CountdownEvent(1);
            using(var queue = new TaskQueue(autoStart: false, queueEmptyAction: () => countdown.Signal(), loggerFactory: Log)) {
                for (int i = 0; i < NumberOfEnqueuedItems; i++) {
                    queue.Enqueue(() => {
                        Interlocked.Increment(ref completed);
                        return Task.CompletedTask;
                    });
                }

                Assert.Equal(NumberOfEnqueuedItems, queue.Queued);
                var sw = Stopwatch.StartNew();
                queue.Start();

                countdown.Wait(TimeSpan.FromSeconds(5));
                _logger.LogInformation("Processed {EnqueuedCount} in {Elapsed:g}", NumberOfEnqueuedItems, sw.Elapsed);
                Assert.Equal(0, countdown.CurrentCount);
                Assert.Equal(0, queue.Queued);
                Assert.Equal(0, queue.Working);
                Assert.Equal(NumberOfEnqueuedItems, completed);
            }
        }

        [Fact]
        public void CanProcessInParrallelQuickly() {
            Log.MinimumLevel = LogLevel.Debug;
            const int NumberOfEnqueuedItems = 1000;
            const int MaxDegreeOfParallelism = 2;

            int completed = 0;
            var countdown = new CountdownEvent(1);
            using(var queue = new TaskQueue(maxDegreeOfParallelism: MaxDegreeOfParallelism, autoStart: false, queueEmptyAction: () => countdown.Signal(), loggerFactory: Log)) {
                for (int i = 0; i < NumberOfEnqueuedItems; i++) {
                    queue.Enqueue(() => {
                        Interlocked.Increment(ref completed);
                        return Task.CompletedTask;
                    });
                }

                Assert.Equal(NumberOfEnqueuedItems, queue.Queued);

                var sw = Stopwatch.StartNew();
                queue.Start();

                countdown.Wait(TimeSpan.FromSeconds(5));
                _logger.LogInformation("Processed {EnqueuedCount} in {Elapsed:g}", NumberOfEnqueuedItems, sw.Elapsed);
                Assert.InRange(countdown.CurrentCount, -1, 0); // TODO: There is a possibility where on completed could be called twice.
                Assert.Equal(0, queue.Queued);
                Assert.Equal(0, queue.Working);
                Assert.Equal(NumberOfEnqueuedItems, completed);
            }
        }

        [Fact]
        public void CanProcessInParrallelQuicklyWithRandomDelays() {
            Log.MinimumLevel = LogLevel.Debug;
            const int NumberOfEnqueuedItems = 500;
            const int MaxDelayInMilliseconds = 10;
            const int MaxDegreeOfParallelism = 4;
            
            int completed = 0;
            var countdown = new CountdownEvent(1);
            using(var queue = new TaskQueue(maxDegreeOfParallelism: MaxDegreeOfParallelism, autoStart: false, queueEmptyAction: () => countdown.Signal(), loggerFactory: Log)) {
                for (int i = 0; i < NumberOfEnqueuedItems; i++) {
                    queue.Enqueue(async () => {
                        var delay = TimeSpan.FromMilliseconds(Exceptionless.RandomData.GetInt(0, MaxDelayInMilliseconds));
                        await SystemClock.SleepAsync(delay);
                        Interlocked.Increment(ref completed);
                    });
                }

                Assert.Equal(NumberOfEnqueuedItems, queue.Queued);
                
                var sw = Stopwatch.StartNew();
                queue.Start();

                countdown.Wait(TimeSpan.FromSeconds(NumberOfEnqueuedItems * MaxDelayInMilliseconds));
                _logger.LogInformation("Processed {EnqueuedCount} in {Elapsed:g}", NumberOfEnqueuedItems, sw.Elapsed);
                Assert.Equal(0, countdown.CurrentCount);
                Assert.Equal(0, queue.Queued);
                Assert.Equal(0, queue.Working);
                Assert.Equal(NumberOfEnqueuedItems, completed);
            }
        }

        //TODO: Can cancel slow tasks

        [Fact]
        public void Benchmark() {
            var summary = BenchmarkDotNet.Running.BenchmarkRunner.Run<TaskQueueBenchmark>();
            _logger.LogInformation(summary.ToJson());
        }
    }

    [MemoryDiagnoser]
    [ShortRunJob]
    public class TaskQueueBenchmark {
        [Params(1, 2, 4)]
        public byte MaxDegreeOfParallelism { get; set; }

        private readonly CountdownEvent _countdown = new CountdownEvent(1);

        [Benchmark]
        public void Run() {
            _countdown.Reset();
            using (var queue = new TaskQueue(autoStart: false, maxDegreeOfParallelism: MaxDegreeOfParallelism, queueEmptyAction: () => _countdown.Signal())) {
                for (int i = 0; i < 100; i++)
                    queue.Enqueue(() => Task.CompletedTask);

                queue.Start();
                _countdown.Wait(5000);
            }
        }
    }
}