using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Attributes.Jobs;
using Foundatio.Logging.Xunit;
using Foundatio.Queues;
using Foundatio.TestHarness.Utility;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Queue {
    public class TaskQueueTests : TestWithLoggingBase {
        public TaskQueueTests(ITestOutputHelper output) : base(output) {
            Log.SetLogLevel<TaskQueue>(LogLevel.Trace);
            Log.SetLogLevel<TaskQueueTests>(LogLevel.Trace);
        }

        [Fact]
        public async Task CanProcessEmptyQueue() {
            Log.MinimumLevel = LogLevel.Trace;
            var queue = new TaskQueue(loggerFactory: Log);
            await queue.RunAsync();
        }

        [Fact]
        public async Task CanRespectMaxItems() {
            Log.MinimumLevel = LogLevel.Trace;
            var queue = new TaskQueue(maxItems: 2, loggerFactory: Log);
            _logger.LogTrace("Enqueueing Task with Delay of 10ms");
            queue.Enqueue(async () => {
                _logger.LogTrace("Starting Delay for 10ms");
                await Task.Delay(10);
                _logger.LogTrace("Finished Delay for 10ms");
            });
            _logger.LogTrace("Enqueueing Task with Delay of 20ms");
            queue.Enqueue(async () => {
                _logger.LogTrace("Starting Delay for 20ms");
                await Task.Delay(20);
                _logger.LogTrace("Finished Delay for 20ms");
            });
            _logger.LogTrace("Enqueueing Task with Delay of 30ms");
            queue.Enqueue(async () => {
                _logger.LogTrace("Starting Delay for 30ms");
                await Task.Delay(30);
                _logger.LogTrace("Finished Delay for 30ms");
            });

            Assert.Equal(2, queue.Queued);

            var sw = Stopwatch.StartNew();
            await queue.RunAsync();
            Assert.InRange(sw.ElapsedMilliseconds, 15, 70);
        }

        [Fact]
        public async Task CanHandleTaskFailure() {
            Log.MinimumLevel = LogLevel.Trace;
            var queue = new TaskQueue(maxItems: 2, loggerFactory: Log);
            _logger.LogTrace("Enqueueing Task with Delay of 10ms");
            queue.Enqueue(async () => {
                _logger.LogTrace("Starting Delay for 10ms");
                await Task.Delay(10);
                throw new Exception("Exception in Queued Task");
            });
            _logger.LogTrace("Enqueueing Task with Delay of 20ms");
            queue.Enqueue(async () => {
                _logger.LogTrace("Starting Delay for 20ms");
                await Task.Delay(20);
                _logger.LogTrace("Finished Delay for 20ms");
            });

            Assert.Equal(2, queue.Queued);

            var sw = Stopwatch.StartNew();
            await queue.RunAsync();
            Assert.InRange(sw.ElapsedMilliseconds, 15, 100);
        }

        [Fact]
        public async Task CanRunInParallel() {
            Log.MinimumLevel = LogLevel.Trace;
            var queue = new TaskQueue(maxDegreeOfParallelism: 2, loggerFactory: Log);
            int completed = 0;
            queue.Enqueue(async () => { await Task.Delay(30); completed++; });
            queue.Enqueue(async () => { await Task.Delay(40); completed++; });
            queue.Enqueue(async () => { await Task.Delay(50); completed++; });
            Assert.Equal(3, queue.Queued);
            Assert.Equal(0, queue.Working);
            Assert.Equal(0, queue.Workers);

            var unawaited = queue.RunAsync();
            await Task.Delay(10);
            Assert.Equal(1, queue.Queued);
            Assert.Equal(2, queue.Working);
            Assert.Equal(2, queue.Workers);

            await unawaited;
            Assert.Equal(0, queue.Queued);
            Assert.Equal(0, queue.Working);
            Assert.Equal(0, queue.Workers);
            Assert.Equal(3, completed);
        }

        [Fact]
        public async Task CanRunContinously() {
            Log.MinimumLevel = LogLevel.Trace;
            var queue = new TaskQueue(maxDegreeOfParallelism: 1, loggerFactory: Log);

            var cancellationTokenSource = new CancellationTokenSource();
            queue.RunContinuous(cancellationTokenSource.Token);
            await Task.Delay(20);
            _logger.LogInformation("Checking worker count");
            Assert.Equal(1, queue.Workers);

            int completed = 0;
            queue.Enqueue(async () => { await Task.Delay(10); completed++; });
            queue.Enqueue(async () => { await Task.Delay(10); completed++; });
            queue.Enqueue(async () => { await Task.Delay(10); completed++; });
            Assert.InRange(queue.Queued, 1, 3);
            Assert.Equal(1, queue.Workers);
            Assert.InRange(queue.Working, 0, 1);
            
            await Task.Delay(50);
            cancellationTokenSource.Cancel();
            await Task.Delay(50);
            Assert.Equal(0, queue.Working);
            Assert.Equal(0, queue.Workers);
            Assert.Equal(0, queue.Queued);
            Assert.Equal(3, completed);
        }

        [Fact]
        public async Task CanProcessQuickly() {
            Log.MinimumLevel = LogLevel.Trace;
            const int NumberOfEnqueuedItems = 1000;
            var queue = new TaskQueue(loggerFactory: Log);

            for (int i = 0; i < NumberOfEnqueuedItems; i++) {
                queue.Enqueue(() => {
                    _logger.LogTrace("Finished task #{TaskId}", i);
                    return Task.CompletedTask;
                });
            }

            Assert.Equal(NumberOfEnqueuedItems, queue.Queued);

            var sw = Stopwatch.StartNew();
            await queue.RunAsync();
            Assert.InRange(sw.ElapsedMilliseconds, 15, 200);
        }

        [Fact]
        public async Task CanProcessQuicklyWithRandomDelays() {
            Log.MinimumLevel = LogLevel.Trace;
            const int NumberOfEnqueuedItems = 1000;
            const int MaxDelayInMilliseconds = 20;
            var queue = new TaskQueue(loggerFactory: Log);

            for (int i = 0; i < NumberOfEnqueuedItems; i++) {
                queue.Enqueue(async () => {
                    var delay = TimeSpan.FromMilliseconds(Exceptionless.RandomData.GetInt(0, MaxDelayInMilliseconds));
                    await Task.Delay(delay);
                    _logger.LogTrace("Finished task #{TaskId} with Delay for {Delay:g}", i, delay);
                });
            }

            Assert.Equal(NumberOfEnqueuedItems, queue.Queued);

            var sw = Stopwatch.StartNew();
            await queue.RunAsync();
            Assert.InRange(sw.ElapsedMilliseconds, NumberOfEnqueuedItems, NumberOfEnqueuedItems * MaxDelayInMilliseconds);
        }

        [Fact]
        public async Task CanProcessInParrallelQuicklyWithRandomDelays() {
            Log.MinimumLevel = LogLevel.Trace;
            const int NumberOfEnqueuedItems = 1000;
            const int MaxDelayInMilliseconds = 20;
            const int MaxDegreeOfParallelism = 4;
            var queue = new TaskQueue(maxDegreeOfParallelism: MaxDegreeOfParallelism, loggerFactory: Log);

            for (int i = 0; i < NumberOfEnqueuedItems; i++) {
                queue.Enqueue(async () => {
                    var delay = TimeSpan.FromMilliseconds(Exceptionless.RandomData.GetInt(0, MaxDelayInMilliseconds));
                    await Task.Delay(delay);
                    _logger.LogTrace("Finished task #{TaskId} with Delay for {Delay:g}", i, delay);
                });
            }

            Assert.Equal(NumberOfEnqueuedItems, queue.Queued);

            var sw = Stopwatch.StartNew();
            await queue.RunAsync();
            Assert.InRange(sw.ElapsedMilliseconds, NumberOfEnqueuedItems / MaxDegreeOfParallelism, NumberOfEnqueuedItems * MaxDelayInMilliseconds / MaxDegreeOfParallelism);
        }

        // TODO: Can cancel slow tasks
        // TODO: Cancel/dispose

        [Fact] 
        public void Benchmark() { 
            var summary = BenchmarkDotNet.Running.BenchmarkRunner.Run<TaskQueueBenchmark>(); 
            _logger.LogInformation(summary.ToJson()); 
        } 
    }

    [MemoryDiagnoser] 
    [ShortRunJob] 
    public class TaskQueueBenchmark {
        private TaskQueue _queue;

        [Params(1, 2)]
        public byte MaxDegreeOfParallelism { get; set; }

        [GlobalSetup] 
        public void Setup() { 
            _queue = new TaskQueue(maxDegreeOfParallelism: MaxDegreeOfParallelism);
            for (int i = 0; i < 100; i++) {
                _queue.Enqueue(() => Task.CompletedTask);
            }
        } 
 
        [Benchmark] 
        public Task Run() { 
            return _queue.RunAsync(); 
        }
    } 
}