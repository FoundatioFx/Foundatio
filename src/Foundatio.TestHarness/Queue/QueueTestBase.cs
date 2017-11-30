using Exceptionless;
using Foundatio.AsyncEx;
using Foundatio.Caching;
using Foundatio.Jobs;
using Foundatio.Lock;
using Foundatio.Logging.Xunit;
using Foundatio.Messaging;
using Foundatio.Metrics;
using Foundatio.Queues;
using Foundatio.Utility;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
#pragma warning disable CS4014
#pragma warning disable AsyncFixer02

namespace Foundatio.Tests.Queue {
    public abstract class QueueTestBase : TestWithLoggingBase, IDisposable {
        protected QueueTestBase(ITestOutputHelper output) : base(output) {
            Log.SetLogLevel<InMemoryCacheClient>(LogLevel.Debug);
            Log.SetLogLevel<InMemoryMetricsClient>(LogLevel.Debug);
            Log.SetLogLevel<MetricsQueueBehavior<SimpleWorkItem>>(LogLevel.Debug);
            Log.SetLogLevel<ScheduledTimer>(LogLevel.Debug);
        }

        protected virtual IQueue<SimpleWorkItem> GetQueue(int retries = 1, TimeSpan? workItemTimeout = null, TimeSpan? retryDelay = null, int deadLetterMaxItems = 100, bool runQueueMaintenance = true) {
            return null;
        }

        protected virtual async Task CleanupQueueAsync(IQueue<SimpleWorkItem> queue) {
            if (queue == null)
                return;

            try {
                await queue.DeleteQueueAsync();
            }
            catch (Exception ex) {
                if (_logger.IsEnabled(LogLevel.Error))
                    _logger.LogError(ex, "Error cleaning up queue");
            }
            finally {
                queue.Dispose();
            }
        }

        public virtual async Task CanQueueAndDequeueWorkItemAsync() {
            var queue = GetQueue();
            if (queue == null)
                return;

            try {
                await queue.DeleteQueueAsync();
                await AssertEmptyQueueAsync(queue);

                await queue.EnqueueAsync(new SimpleWorkItem {
                    Data = "Hello"
                });
                Assert.Equal(1, (await queue.GetQueueStatsAsync()).Enqueued);

                var workItem = await queue.DequeueAsync(TimeSpan.Zero);
                Assert.NotNull(workItem);
                Assert.Equal("Hello", workItem.Value.Data);
                Assert.Equal(1, (await queue.GetQueueStatsAsync()).Dequeued);

                await workItem.CompleteAsync();
                Assert.False(workItem.IsAbandoned);
                Assert.True(workItem.IsCompleted);
                var stats = await queue.GetQueueStatsAsync();
                Assert.Equal(1, stats.Completed);
                Assert.Equal(0, stats.Queued);

            }
            finally {
                await CleanupQueueAsync(queue);
            }
        }

        /// <summary>
        /// When a cancelled token is passed into Dequeue, it will only try to dequeue one time and then exit.
        /// </summary>
        /// <returns></returns>
        public virtual async Task CanDequeueWithCancelledTokenAsync() {
            var queue = GetQueue();
            if (queue == null)
                return;

            try {
                await queue.DeleteQueueAsync();
                await AssertEmptyQueueAsync(queue);

                await queue.EnqueueAsync(new SimpleWorkItem {
                    Data = "Hello"
                });
                Assert.Equal(1, (await queue.GetQueueStatsAsync()).Enqueued);

                var workItem = await queue.DequeueAsync(new CancellationToken(true));
                Assert.NotNull(workItem);
                Assert.Equal("Hello", workItem.Value.Data);
                Assert.Equal(1, (await queue.GetQueueStatsAsync()).Dequeued);

                // TODO: We should verify that only one retry occurred.
                await workItem.CompleteAsync();
                var stats = await queue.GetQueueStatsAsync();
                Assert.Equal(1, stats.Completed);
                Assert.Equal(0, stats.Queued);
            }
            finally {
                await CleanupQueueAsync(queue);
            }
        }

        public virtual async Task CanDequeueEfficientlyAsync() {
            const int iterations = 100;

            var queue = GetQueue(runQueueMaintenance: false);
            if (queue == null)
                return;

            try {
                await queue.DeleteQueueAsync();
                await AssertEmptyQueueAsync(queue);
                await queue.EnqueueAsync(new SimpleWorkItem { Data = "Initialize queue to create more accurate metrics" });
                Assert.NotNull(await queue.DequeueAsync(TimeSpan.FromSeconds(1)));

                using (var metrics = new InMemoryMetricsClient(new InMemoryMetricsClientOptions())) {
                    queue.AttachBehavior(new MetricsQueueBehavior<SimpleWorkItem>(metrics, reportCountsInterval: TimeSpan.FromMilliseconds(100), loggerFactory: Log));

                    Task.Run(async () => {
                        _logger.LogTrace("Starting enqueue loop.");
                        for (int index = 0; index < iterations; index++) {
                            await SystemClock.SleepAsync(RandomData.GetInt(10, 30));
                            await queue.EnqueueAsync(new SimpleWorkItem { Data = "Hello" });
                        }
                        _logger.LogTrace("Finished enqueuing.");
                    });

                    _logger.LogTrace("Starting dequeue loop.");
                    for (int index = 0; index < iterations; index++) {
                        var item = await queue.DequeueAsync(TimeSpan.FromSeconds(3));
                        Assert.NotNull(item);
                        await item.CompleteAsync();
                    }
                    _logger.LogTrace("Finished dequeuing.");

                    await metrics.FlushAsync();
                    var timing = await metrics.GetTimerStatsAsync("simpleworkitem.queuetime");
                    if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("AverageDuration: {AverageDuration}", timing.AverageDuration);
                    Assert.InRange(timing.AverageDuration, 0, 75);
                }
            }
            finally {
                await CleanupQueueAsync(queue);
            }
        }

        public virtual async Task CanResumeDequeueEfficientlyAsync() {
            const int iterations = 10;

            var queue = GetQueue(runQueueMaintenance: false);
            if (queue == null)
                return;

            try {
                await queue.DeleteQueueAsync();
                await AssertEmptyQueueAsync(queue);

                using (var metrics = new InMemoryMetricsClient(new InMemoryMetricsClientOptions())) {
                    for (int index = 0; index < iterations; index++)
                        await queue.EnqueueAsync(new SimpleWorkItem { Data = "Hello" });

                    using (var secondQueue = GetQueue(runQueueMaintenance: false)) {
                        secondQueue.AttachBehavior(new MetricsQueueBehavior<SimpleWorkItem>(metrics, reportCountsInterval: TimeSpan.FromMilliseconds(100), loggerFactory: Log));

                        _logger.LogTrace("Starting dequeue loop.");
                        for (int index = 0; index < iterations; index++) {
                            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("[{Index}] Calling Dequeue", index);
                            var item = await secondQueue.DequeueAsync(TimeSpan.FromSeconds(3));
                            Assert.NotNull(item);
                            await item.CompleteAsync();
                        }

                        await metrics.FlushAsync(); // This won't flush metrics queue behaviors
                        var timing = await metrics.GetTimerStatsAsync("simpleworkitem.queuetime");
                        if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("TotalDuration: {TotalDuration} AverageDuration: {AverageDuration}", timing.TotalDuration, timing.AverageDuration);
                        Assert.InRange(timing.AverageDuration, 0, 75);
                    }
                }
            }
            finally {
                await CleanupQueueAsync(queue);
            }
        }

        public virtual async Task CanQueueAndDequeueMultipleWorkItemsAsync() {
            var queue = GetQueue();
            if (queue == null)
                return;

            try {
                await queue.DeleteQueueAsync();
                await AssertEmptyQueueAsync(queue);

                const int workItemCount = 25;
                for (int i = 0; i < workItemCount; i++) {
                    await queue.EnqueueAsync(new SimpleWorkItem {
                        Data = "Hello"
                    });
                }
                Assert.Equal(workItemCount, (await queue.GetQueueStatsAsync()).Queued);

                var sw = Stopwatch.StartNew();
                for (int i = 0; i < workItemCount; i++) {
                    var workItem = await queue.DequeueAsync(TimeSpan.FromSeconds(5));
                    Assert.NotNull(workItem);
                    Assert.Equal("Hello", workItem.Value.Data);
                    await workItem.CompleteAsync();
                }
                sw.Stop();
                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Time {Elapsed:g}", sw.Elapsed);
                Assert.InRange(sw.Elapsed.TotalSeconds, 0, 5);

                var stats = await queue.GetQueueStatsAsync();
                Assert.Equal(workItemCount, stats.Dequeued);
                Assert.Equal(workItemCount, stats.Completed);
                Assert.Equal(0, stats.Queued);
            }
            finally {
                await CleanupQueueAsync(queue);
            }
        }

        public virtual async Task WillNotWaitForItemAsync() {
            var queue = GetQueue();
            if (queue == null)
                return;

            try {
                await queue.DeleteQueueAsync();
                await AssertEmptyQueueAsync(queue);

                var sw = Stopwatch.StartNew();
                var workItem = await queue.DequeueAsync(TimeSpan.Zero);
                sw.Stop();
                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Time {Elapsed:g}", sw.Elapsed);
                Assert.Null(workItem);
                Assert.InRange(sw.Elapsed.TotalMilliseconds, 0, 100);
            }
            finally {
                await CleanupQueueAsync(queue);
            }
        }

        public virtual async Task WillWaitForItemAsync() {
            var queue = GetQueue();
            if (queue == null)
                return;

            try {
                await queue.DeleteQueueAsync();
                await AssertEmptyQueueAsync(queue);

                var sw = Stopwatch.StartNew();
                var workItem = await queue.DequeueAsync(TimeSpan.FromMilliseconds(100));
                sw.Stop();
                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Time {Elapsed:g}", sw.Elapsed);
                Assert.Null(workItem);
                Assert.True(sw.Elapsed > TimeSpan.FromMilliseconds(100));

                Task.Run(async () => {
                    await SystemClock.SleepAsync(500);
                    await queue.EnqueueAsync(new SimpleWorkItem {
                        Data = "Hello"
                    });
                });

                sw.Restart();
                workItem = await queue.DequeueAsync(TimeSpan.FromSeconds(1));
                sw.Stop();
                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Time {Elapsed:g}", sw.Elapsed);
                Assert.True(sw.Elapsed > TimeSpan.FromMilliseconds(400));
                Assert.NotNull(workItem);
                await workItem.CompleteAsync();

            }
            finally {
                await CleanupQueueAsync(queue);
            }
        }

        public virtual async Task DequeueWaitWillGetSignaledAsync() {
            var queue = GetQueue();
            if (queue == null)
                return;

            try {
                await queue.DeleteQueueAsync();
                await AssertEmptyQueueAsync(queue);

                Task.Run(async () => {
                    await SystemClock.SleepAsync(250);
                    await queue.EnqueueAsync(new SimpleWorkItem {
                        Data = "Hello"
                    });
                });

                var sw = Stopwatch.StartNew();
                var workItem = await queue.DequeueAsync(TimeSpan.FromSeconds(2));
                sw.Stop();
                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Time {Elapsed:g}", sw.Elapsed);
                Assert.NotNull(workItem);
                Assert.InRange(sw.Elapsed.TotalSeconds, 0, 2);
            }
            finally {
                await CleanupQueueAsync(queue);
            }
        }

        public virtual async Task CanUseQueueWorkerAsync() {
            var queue = GetQueue();
            if (queue == null)
                return;

            try {
                await queue.DeleteQueueAsync();
                await AssertEmptyQueueAsync(queue);

                var resetEvent = new AsyncManualResetEvent(false);
                await queue.StartWorkingAsync(async w => {
                    Assert.Equal("Hello", w.Value.Data);
                    await w.CompleteAsync();
                    resetEvent.Set();
                });

                await queue.EnqueueAsync(new SimpleWorkItem {
                    Data = "Hello"
                });

                await resetEvent.WaitAsync();
                var stats = await queue.GetQueueStatsAsync();
                Assert.Equal(1, stats.Completed);
                Assert.Equal(0, stats.Queued);
                Assert.Equal(0, stats.Errors);
            }
            finally {
                await CleanupQueueAsync(queue);
            }
        }

        public virtual async Task CanHandleErrorInWorkerAsync() {
            var queue = GetQueue(retries: 0);
            if (queue == null)
                return;

            try {
                await queue.DeleteQueueAsync();
                await AssertEmptyQueueAsync(queue);

                using (var metrics = new InMemoryMetricsClient(new InMemoryMetricsClientOptions { Buffered = false, LoggerFactory = Log })) {
                    queue.AttachBehavior(new MetricsQueueBehavior<SimpleWorkItem>(metrics, reportCountsInterval: TimeSpan.FromMilliseconds(100), loggerFactory: Log));
                    await queue.StartWorkingAsync(w => {
                        _logger.LogDebug("WorkAction");
                        Assert.Equal("Hello", w.Value.Data);
                        throw new Exception();
                    });

                    var resetEvent = new AsyncManualResetEvent(false);
                    using (queue.Abandoned.AddSyncHandler((o, args) => resetEvent.Set())) {
                        await queue.EnqueueAsync(new SimpleWorkItem { Data = "Hello" });
                        await resetEvent.WaitAsync(TimeSpan.FromSeconds(200));

                        await SystemClock.SleepAsync(100); // give time for the stats to reflect the changes.
                        var stats = await queue.GetQueueStatsAsync();
                        if (_logger.IsEnabled(LogLevel.Information))
                            _logger.LogInformation("Completed: {Completed} Errors: {Errors} Deadletter: {Deadletter} Working: {Working} ", stats.Completed, stats.Errors, stats.Deadletter, stats.Working);
                        Assert.Equal(0, stats.Completed);
                        Assert.Equal(1, stats.Errors);
                        Assert.Equal(1, stats.Deadletter);
                    }
                }
            }
            finally {
                await CleanupQueueAsync(queue);
            }
        }

        public virtual async Task WorkItemsWillTimeoutAsync() {
            var queue = GetQueue(retryDelay: TimeSpan.Zero, workItemTimeout: TimeSpan.FromMilliseconds(50));
            if (queue == null)
                return;

            try {
                await queue.DeleteQueueAsync();
                await AssertEmptyQueueAsync(queue);

                await queue.EnqueueAsync(new SimpleWorkItem {
                    Data = "Hello"
                });
                var workItem = await queue.DequeueAsync(TimeSpan.Zero);
                Assert.NotNull(workItem);
                Assert.Equal("Hello", workItem.Value.Data);
                await SystemClock.SleepAsync(TimeSpan.FromSeconds(1));

                // wait for the task to be auto abandoned

                var sw = Stopwatch.StartNew();
                workItem = await queue.DequeueAsync(TimeSpan.FromSeconds(5));
                sw.Stop();
                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Time {Elapsed:g}", sw.Elapsed);
                Assert.NotNull(workItem);
                await workItem.CompleteAsync();
                Assert.Equal(0, (await queue.GetQueueStatsAsync()).Queued);
            }
            finally {
                await CleanupQueueAsync(queue);
            }
        }

        public virtual async Task WorkItemsWillGetMovedToDeadletterAsync() {
            var queue = GetQueue(retryDelay: TimeSpan.Zero);
            if (queue == null)
                return;

            try {
                await queue.DeleteQueueAsync();
                await AssertEmptyQueueAsync(queue);

                await queue.EnqueueAsync(new SimpleWorkItem {
                    Data = "Hello"
                });
                var workItem = await queue.DequeueAsync(TimeSpan.Zero);
                Assert.Equal("Hello", workItem.Value.Data);
                Assert.Equal(1, (await queue.GetQueueStatsAsync()).Dequeued);

                await workItem.AbandonAsync();
                Assert.Equal(1, (await queue.GetQueueStatsAsync()).Abandoned);

                // work item should be retried 1 time.
                workItem = await queue.DequeueAsync(TimeSpan.FromSeconds(5));
                Assert.NotNull(workItem);
                Assert.Equal("Hello", workItem.Value.Data);
                Assert.Equal(2, (await queue.GetQueueStatsAsync()).Dequeued);

                await workItem.AbandonAsync();

                // work item should be moved to deadletter _queue after retries.
                var stats = await queue.GetQueueStatsAsync();
                Assert.Equal(1, stats.Deadletter);
                Assert.Equal(2, stats.Abandoned);
            }
            finally {
                await CleanupQueueAsync(queue);
            }
        }

        public virtual async Task CanAutoCompleteWorkerAsync() {
            var queue = GetQueue();
            if (queue == null)
                return;

            try {
                await queue.DeleteQueueAsync();
                await AssertEmptyQueueAsync(queue);

                var resetEvent = new AsyncManualResetEvent(false);
                await queue.StartWorkingAsync(w => {
                    Assert.Equal("Hello", w.Value.Data);
                    return Task.CompletedTask;
                }, true);

                using (queue.Completed.AddSyncHandler((s, e) => { resetEvent.Set(); })) {
                    await queue.EnqueueAsync(new SimpleWorkItem { Data = "Hello" });

                    Assert.Equal(1, (await queue.GetQueueStatsAsync()).Enqueued);
                    await resetEvent.WaitAsync(TimeSpan.FromSeconds(2));

                    var stats = await queue.GetQueueStatsAsync();
                    Assert.Equal(0, stats.Queued);
                    Assert.Equal(0, stats.Errors);
                    Assert.Equal(1, stats.Completed);
                }
            }
            finally {
                await CleanupQueueAsync(queue);
            }
        }

        public virtual async Task CanHaveMultipleQueueInstancesAsync() {
            var queue = GetQueue(retries: 0, retryDelay: TimeSpan.Zero);
            if (queue == null)
                return;

            try {
                await queue.DeleteQueueAsync();
                await AssertEmptyQueueAsync(queue);

                const int workItemCount = 500;
                const int workerCount = 3;
                var countdown = new AsyncCountdownEvent(workItemCount);
                var info = new WorkInfo();
                var workers = new List<IQueue<SimpleWorkItem>> { queue };

                try {
                    for (int i = 0; i < workerCount; i++) {
                        var q = GetQueue(retries: 0, retryDelay: TimeSpan.Zero);
                        if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Queue Id: {Id}, I: {Instance}", q.QueueId, i);
                        await q.StartWorkingAsync(w => DoWorkAsync(w, countdown, info));
                        workers.Add(q);
                    }

                    await Run.InParallelAsync(workItemCount, async i => {
                        string id = await queue.EnqueueAsync(new SimpleWorkItem {
                            Data = "Hello",
                            Id = i
                        });
                        if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Enqueued Index: {Instance} Id: {Id}", i, id);
                    });

                    await countdown.WaitAsync();
                    await SystemClock.SleepAsync(50);
                    if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Completed: {Completed} Abandoned: {Abandoned} Error: {Errors}",
                        info.CompletedCount,
                        info.AbandonCount,
                        info.ErrorCount);

                    if (_logger.IsEnabled(LogLevel.Information))
                        _logger.LogInformation("Work Info Stats: Completed: {Completed} Abandoned: {Abandoned} Error: {Errors}", info.CompletedCount, info.AbandonCount, info.ErrorCount);
                    Assert.Equal(workItemCount, info.CompletedCount + info.AbandonCount + info.ErrorCount);

                    // In memory queue doesn't share state.
                    if (queue.GetType() == typeof(InMemoryQueue<SimpleWorkItem>)) {
                        var stats = await queue.GetQueueStatsAsync();
                        Assert.Equal(0, stats.Working);
                        Assert.Equal(0, stats.Timeouts);
                        Assert.Equal(workItemCount, stats.Enqueued);
                        Assert.Equal(workItemCount, stats.Dequeued);
                        Assert.Equal(info.CompletedCount, stats.Completed);
                        Assert.Equal(info.ErrorCount, stats.Errors);
                        Assert.Equal(info.AbandonCount, stats.Abandoned - info.ErrorCount);
                        Assert.Equal(info.AbandonCount + stats.Errors, stats.Deadletter);
                    }
                    else {
                        var workerStats = new List<QueueStats>();
                        for (int i = 0; i < workers.Count; i++) {
                            var stats = await workers[i].GetQueueStatsAsync();
                            if (_logger.IsEnabled(LogLevel.Information))
                                _logger.LogInformation("Worker#{Id} Working: {Working} Completed: {Completed} Abandoned: {Abandoned} Error: {Errors} Deadletter: {Deadletter}", i, stats.Working, stats.Completed, stats.Abandoned, stats.Errors, stats.Deadletter);
                            workerStats.Add(stats);
                        }

                        Assert.Equal(info.CompletedCount, workerStats.Sum(s => s.Completed));
                        Assert.Equal(info.ErrorCount, workerStats.Sum(s => s.Errors));
                        Assert.Equal(info.AbandonCount, workerStats.Sum(s => s.Abandoned) - info.ErrorCount);
                        Assert.Equal(info.AbandonCount + workerStats.Sum(s => s.Errors), (workerStats.LastOrDefault()?.Deadletter ?? 0));
                    }
                }
                finally {
                    foreach (var q in workers)
                        await CleanupQueueAsync(q);
                }
            }
            finally {
                await CleanupQueueAsync(queue);
            }
        }

        public virtual async Task CanDelayRetryAsync() {
            var queue = GetQueue(workItemTimeout: TimeSpan.FromMilliseconds(500), retryDelay: TimeSpan.FromSeconds(1));
            if (queue == null)
                return;

            try {
                await queue.DeleteQueueAsync();
                await AssertEmptyQueueAsync(queue);

                await queue.EnqueueAsync(new SimpleWorkItem {
                    Data = "Hello"
                });

                var workItem = await queue.DequeueAsync(TimeSpan.Zero);
                Assert.NotNull(workItem);
                Assert.Equal("Hello", workItem.Value.Data);

                var startTime = SystemClock.UtcNow;
                await workItem.AbandonAsync();
                Assert.Equal(1, (await queue.GetQueueStatsAsync()).Abandoned);

                workItem = await queue.DequeueAsync(TimeSpan.FromSeconds(5));
                var elapsed = SystemClock.UtcNow.Subtract(startTime);
                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Time {Elapsed}", elapsed);
                Assert.NotNull(workItem);
                Assert.True(elapsed > TimeSpan.FromSeconds(.95));
                await workItem.CompleteAsync();
                Assert.Equal(0, (await queue.GetQueueStatsAsync()).Queued);
            }
            finally {
                await CleanupQueueAsync(queue);
            }
        }

        public virtual async Task CanRunWorkItemWithMetricsAsync() {
            int completedCount = 0;

            using (var metrics = new InMemoryMetricsClient(new InMemoryMetricsClientOptions { Buffered = false, LoggerFactory = Log })) {
                var behavior = new MetricsQueueBehavior<WorkItemData>(metrics, "metric", TimeSpan.FromMilliseconds(100), loggerFactory: Log);
                var options = new InMemoryQueueOptions<WorkItemData> { Behaviors = new[] { behavior }, LoggerFactory = Log };
                using (var queue = new InMemoryQueue<WorkItemData>(options)) {
                    Func<object, CompletedEventArgs<WorkItemData>, Task> handler = (sender, e) => {
                        completedCount++;
                        return Task.CompletedTask;
                    };

                    using (queue.Completed.AddHandler(handler)) {
                        _logger.LogTrace("Before enqueue");
                        await queue.EnqueueAsync(new SimpleWorkItem { Id = 1, Data = "Testing" });
                        await queue.EnqueueAsync(new SimpleWorkItem { Id = 2, Data = "Testing" });
                        await queue.EnqueueAsync(new SimpleWorkItem { Id = 3, Data = "Testing" });

                        await SystemClock.SleepAsync(100);

                        _logger.LogTrace("Before dequeue");
                        var item = await queue.DequeueAsync();
                        await item.CompleteAsync();

                        item = await queue.DequeueAsync();
                        await item.CompleteAsync();

                        item = await queue.DequeueAsync();
                        await item.AbandonAsync();

                        _logger.LogTrace("Before asserts");
                        Assert.Equal(2, completedCount);

                        await SystemClock.SleepAsync(100); // flush metrics queue behaviors
                        await metrics.FlushAsync();
                        Assert.InRange((await metrics.GetGaugeStatsAsync("metric.workitemdata.count")).Max, 1, 3);
                        Assert.InRange((await metrics.GetGaugeStatsAsync("metric.workitemdata.working")).Max, 0, 1);

                        Assert.Equal(3, await metrics.GetCounterCountAsync("metric.workitemdata.simple.enqueued"));
                        Assert.Equal(3, await metrics.GetCounterCountAsync("metric.workitemdata.enqueued"));

                        Assert.Equal(3, await metrics.GetCounterCountAsync("metric.workitemdata.simple.dequeued"));
                        Assert.Equal(3, await metrics.GetCounterCountAsync("metric.workitemdata.dequeued"));

                        Assert.Equal(2, await metrics.GetCounterCountAsync("metric.workitemdata.simple.completed"));
                        Assert.Equal(2, await metrics.GetCounterCountAsync("metric.workitemdata.completed"));

                        Assert.Equal(1, await metrics.GetCounterCountAsync("metric.workitemdata.simple.abandoned"));
                        Assert.Equal(1, await metrics.GetCounterCountAsync("metric.workitemdata.abandoned"));

                        var queueTiming = await metrics.GetTimerStatsAsync("metric.workitemdata.simple.queuetime");
                        Assert.Equal(3, queueTiming.Count);
                        queueTiming = await metrics.GetTimerStatsAsync("metric.workitemdata.queuetime");
                        Assert.Equal(3, queueTiming.Count);

                        var processTiming = await metrics.GetTimerStatsAsync("metric.workitemdata.simple.processtime");
                        Assert.Equal(3, processTiming.Count);
                        processTiming = await metrics.GetTimerStatsAsync("metric.workitemdata.processtime");
                        Assert.Equal(3, processTiming.Count);

                        var queueStats = await metrics.GetQueueStatsAsync("metric.workitemdata");
                        Assert.Equal(3, queueStats.Enqueued.Count);
                        Assert.Equal(3, queueStats.Dequeued.Count);
                        Assert.Equal(2, queueStats.Completed.Count);
                        Assert.Equal(1, queueStats.Abandoned.Count);
                        Assert.InRange(queueStats.Count.Max, 1, 3);
                        Assert.InRange(queueStats.Working.Max, 0, 1);

                        var subQueueStats = await metrics.GetQueueStatsAsync("metric.workitemdata", "simple");
                        Assert.Equal(3, subQueueStats.Enqueued.Count);
                        Assert.Equal(3, subQueueStats.Dequeued.Count);
                        Assert.Equal(2, subQueueStats.Completed.Count);
                        Assert.Equal(1, subQueueStats.Abandoned.Count);
                    }
                }
            }
        }

        public virtual async Task CanRenewLockAsync() {
            Log.SetLogLevel<InMemoryQueue<SimpleWorkItem>>(LogLevel.Trace);

            // Need large value to reproduce this test
            var workItemTimeout = TimeSpan.FromSeconds(1);
            // Slightly shorter than the timeout to ensure we haven't lost the lock
            var renewWait = TimeSpan.FromSeconds(workItemTimeout.TotalSeconds * .25d);

            var queue = GetQueue(retryDelay: TimeSpan.Zero, workItemTimeout: workItemTimeout);
            if (queue == null)
                return;

            try {
                await queue.DeleteQueueAsync();
                await AssertEmptyQueueAsync(queue);

                await queue.EnqueueAsync(new SimpleWorkItem {
                    Data = "Hello"
                });
                var entry = await queue.DequeueAsync(TimeSpan.Zero);
                Assert.NotNull(entry);
                Assert.Equal("Hello", entry.Value.Data);

                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Waiting for {RenewWait:g} before renewing lock", renewWait);
                await SystemClock.SleepAsync(renewWait);
                _logger.LogTrace("Renewing lock");
                await entry.RenewLockAsync();
                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Waiting for {RenewWait:g} to see if lock was renewed", renewWait);
                await SystemClock.SleepAsync(renewWait);

                // We shouldn't get another item here if RenewLock works.
                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Attempting to dequeue item that shouldn't exist");
                var nullWorkItem = await queue.DequeueAsync(TimeSpan.Zero);
                Assert.Null(nullWorkItem);
                await entry.CompleteAsync();
                Assert.Equal(0, (await queue.GetQueueStatsAsync()).Queued);
            }
            finally {
                await CleanupQueueAsync(queue);
            }
        }

        public virtual async Task CanAbandonQueueEntryOnceAsync() {
            var queue = GetQueue();
            if (queue == null)
                return;

            try {
                await queue.DeleteQueueAsync();
                await AssertEmptyQueueAsync(queue);

                await queue.EnqueueAsync(new SimpleWorkItem { Data = "Hello" });
                Assert.Equal(1, (await queue.GetQueueStatsAsync()).Enqueued);

                var workItem = await queue.DequeueAsync(TimeSpan.Zero);
                Assert.NotNull(workItem);
                Assert.Equal("Hello", workItem.Value.Data);
                Assert.Equal(1, (await queue.GetQueueStatsAsync()).Dequeued);

                await workItem.AbandonAsync();
                Assert.True(workItem.IsAbandoned);
                Assert.False(workItem.IsCompleted);
                await Assert.ThrowsAnyAsync<Exception>(() => workItem.AbandonAsync());
                await Assert.ThrowsAnyAsync<Exception>(() => workItem.CompleteAsync());
                await Assert.ThrowsAnyAsync<Exception>(() => workItem.CompleteAsync());

                var stats = await queue.GetQueueStatsAsync();
                Assert.Equal(1, stats.Abandoned);
                Assert.Equal(0, stats.Completed);
                Assert.Equal(0, stats.Deadletter);
                Assert.Equal(1, stats.Dequeued);
                Assert.Equal(1, stats.Enqueued);
                Assert.Equal(0, stats.Errors);
                Assert.InRange(stats.Queued, 0, 1);
                Assert.Equal(0, stats.Timeouts);
                Assert.Equal(0, stats.Working);

                if (workItem is QueueEntry<SimpleWorkItem> queueEntry)
                    Assert.Equal(1, queueEntry.Attempts);

                await queue.EnqueueAsync(new SimpleWorkItem { Data = "Hello" });
                workItem = await queue.DequeueAsync(TimeSpan.Zero);

                await queue.AbandonAsync(workItem);
                Assert.True(workItem.IsAbandoned);
                Assert.False(workItem.IsCompleted);
                await Assert.ThrowsAnyAsync<Exception>(() => workItem.CompleteAsync());
                await Assert.ThrowsAnyAsync<Exception>(() => workItem.AbandonAsync());
                await Assert.ThrowsAnyAsync<Exception>(() => queue.AbandonAsync(workItem));
                await Assert.ThrowsAnyAsync<Exception>(() => queue.CompleteAsync(workItem));
            }
            finally {
                await CleanupQueueAsync(queue);
            }
        }

        public virtual async Task CanCompleteQueueEntryOnceAsync() {
            var queue = GetQueue();
            if (queue == null)
                return;

            try {
                await queue.DeleteQueueAsync();
                await queue.EnqueueAsync(new SimpleWorkItem { Data = "Hello" });

                Assert.Equal(1, (await queue.GetQueueStatsAsync()).Enqueued);

                var workItem = await queue.DequeueAsync(TimeSpan.Zero);
                Assert.NotNull(workItem);
                Assert.Equal("Hello", workItem.Value.Data);
                Assert.Equal(1, (await queue.GetQueueStatsAsync()).Dequeued);

                await workItem.CompleteAsync();
                await Assert.ThrowsAnyAsync<Exception>(() => workItem.CompleteAsync());
                await Assert.ThrowsAnyAsync<Exception>(() => workItem.AbandonAsync());
                await Assert.ThrowsAnyAsync<Exception>(() => workItem.AbandonAsync());
                var stats = await queue.GetQueueStatsAsync();
                Assert.Equal(0, stats.Abandoned);
                Assert.Equal(1, stats.Completed);
                Assert.Equal(0, stats.Deadletter);
                Assert.Equal(1, stats.Dequeued);
                Assert.Equal(1, stats.Enqueued);
                Assert.Equal(0, stats.Errors);
                Assert.Equal(0, stats.Queued);
                Assert.Equal(0, stats.Timeouts);
                Assert.Equal(0, stats.Working);

                if (workItem is QueueEntry<SimpleWorkItem> queueEntry)
                    Assert.Equal(1, queueEntry.Attempts);
            }
            finally {
                await CleanupQueueAsync(queue);
            }
        }

        public virtual async Task CanDequeueWithLockingAsync() {
            using (var cache = new InMemoryCacheClient(new InMemoryCacheClientOptions { LoggerFactory = Log })) {
                using (var messageBus = new InMemoryMessageBus(new InMemoryMessageBusOptions { LoggerFactory = Log })) {
                    var distributedLock = new CacheLockProvider(cache, messageBus, Log);
                    await CanDequeueWithLockingImpAsync(distributedLock);
                }
            }
        }

        protected async Task CanDequeueWithLockingImpAsync(CacheLockProvider distributedLock) {
            var queue = GetQueue(retryDelay: TimeSpan.Zero, retries: 0);
            if (queue == null)
                return;

            try {
                await queue.DeleteQueueAsync();
                await AssertEmptyQueueAsync(queue);

                using (var metrics = new InMemoryMetricsClient(new InMemoryMetricsClientOptions { Buffered = false, LoggerFactory = Log })) {
                    queue.AttachBehavior(new MetricsQueueBehavior<SimpleWorkItem>(metrics, loggerFactory: Log));

                    var resetEvent = new AsyncAutoResetEvent();
                    await queue.StartWorkingAsync(async w => {
                        if (_logger.IsEnabled(LogLevel.Information)) _logger.LogInformation("Acquiring distributed lock in work item");
                        var l = await distributedLock.AcquireAsync("test");
                        Assert.NotNull(l);
                        if (_logger.IsEnabled(LogLevel.Information)) _logger.LogInformation("Acquired distributed lock");
                        SystemClock.Sleep(TimeSpan.FromMilliseconds(250));
                        await l.ReleaseAsync();
                        if (_logger.IsEnabled(LogLevel.Information)) _logger.LogInformation("Released distributed lock");

                        await w.CompleteAsync();
                        resetEvent.Set();
                    });

                    await queue.EnqueueAsync(new SimpleWorkItem { Data = "Hello" });
                    await resetEvent.WaitAsync(TimeSpan.FromSeconds(5).ToCancellationToken());

                    await SystemClock.SleepAsync(1);
                    var stats = await queue.GetQueueStatsAsync();
                    if (_logger.IsEnabled(LogLevel.Information))
                        _logger.LogInformation("Completed: {Completed} Errors: {Errors} Deadletter: {Deadletter} Working: {Working} ", stats.Completed, stats.Errors, stats.Deadletter, stats.Working);
                    Assert.Equal(1, stats.Completed);
                }
            }
            finally {
                await CleanupQueueAsync(queue);
            }
        }

        public virtual async Task CanHaveMultipleQueueInstancesWithLockingAsync() {
            using (var cache = new InMemoryCacheClient(new InMemoryCacheClientOptions { LoggerFactory = Log })) {
                using (var messageBus = new InMemoryMessageBus(new InMemoryMessageBusOptions { LoggerFactory = Log })) {
                    var distributedLock = new CacheLockProvider(cache, messageBus, Log);
                    await CanHaveMultipleQueueInstancesWithLockingImplAsync(distributedLock);
                }
            }
        }

        protected async Task CanHaveMultipleQueueInstancesWithLockingImplAsync(CacheLockProvider distributedLock) {
            var queue = GetQueue(retries: 0, retryDelay: TimeSpan.Zero);
            if (queue == null)
                return;

            try {
                await queue.DeleteQueueAsync();
                await AssertEmptyQueueAsync(queue);

                const int workItemCount = 16;
                const int workerCount = 4;
                var countdown = new AsyncCountdownEvent(workItemCount);
                var info = new WorkInfo();
                var workers = new List<IQueue<SimpleWorkItem>> { queue };

                try {
                    for (int i = 0; i < workerCount; i++) {
                        var q = GetQueue(retries: 0, retryDelay: TimeSpan.Zero);
                        int instanceCount = i;
                        await q.StartWorkingAsync(async w => {
                            if (_logger.IsEnabled(LogLevel.Information))
                                _logger.LogInformation("[{Instance}] Acquiring distributed lock in work item: {Id}", instanceCount, w.Id);
                            var l = await distributedLock.AcquireAsync("test");
                            Assert.NotNull(l);
                            if (_logger.IsEnabled(LogLevel.Information))
                                _logger.LogInformation("[{Instance}] Acquired distributed lock: {Id}", instanceCount, w.Id);
                            SystemClock.Sleep(TimeSpan.FromMilliseconds(50));
                            await l.ReleaseAsync();
                            if (_logger.IsEnabled(LogLevel.Information))
                                _logger.LogInformation("[{Instance}] Released distributed lock: {Id}", instanceCount, w.Id);

                            await w.CompleteAsync();
                            info.IncrementCompletedCount();
                            countdown.Signal();
                            if (_logger.IsEnabled(LogLevel.Information))
                                _logger.LogInformation("[{Instance}] Signaled countdown: {Id}", instanceCount, w.Id);
                        });
                        workers.Add(q);
                    }

                    await Run.InParallelAsync(workItemCount, async i => {
                        string id = await queue.EnqueueAsync(new SimpleWorkItem {
                            Data = "Hello",
                            Id = i
                        });
                        if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Enqueued Index: {Instance} Id: {Id}", i, id);
                    });

                    await countdown.WaitAsync(TimeSpan.FromSeconds(5).ToCancellationToken());
                    await SystemClock.SleepAsync(50);
                    if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Completed: {Completed} Abandoned: {Abandoned} Error: {Errors}", info.CompletedCount, info.AbandonCount, info.ErrorCount);

                    if (_logger.IsEnabled(LogLevel.Information))
                        _logger.LogInformation("Work Info Stats: Completed: {Completed} Abandoned: {Abandoned} Error: {Errors}", info.CompletedCount, info.AbandonCount, info.ErrorCount);
                    Assert.Equal(workItemCount, info.CompletedCount + info.AbandonCount + info.ErrorCount);

                    // In memory queue doesn't share state.
                    if (queue.GetType() == typeof(InMemoryQueue<SimpleWorkItem>)) {
                        var stats = await queue.GetQueueStatsAsync();
                        Assert.Equal(info.CompletedCount, stats.Completed);
                    }
                    else {
                        var workerStats = new List<QueueStats>();
                        for (int i = 0; i < workers.Count; i++) {
                            var stats = await workers[i].GetQueueStatsAsync();
                            if (_logger.IsEnabled(LogLevel.Information))
                                _logger.LogInformation("Worker#{Id} Working: {Working} Completed: {Completed} Abandoned: {Abandoned} Error: {Errors} Deadletter: {Deadletter}", i, stats.Working, stats.Completed, stats.Abandoned, stats.Errors, stats.Deadletter);
                            workerStats.Add(stats);
                        }

                        Assert.Equal(info.CompletedCount, workerStats.Sum(s => s.Completed));
                    }
                }
                finally {
                    foreach (var q in workers)
                        await CleanupQueueAsync(q);
                }
            }
            finally {
                await CleanupQueueAsync(queue);
            }
        }

        protected async Task DoWorkAsync(IQueueEntry<SimpleWorkItem> w, AsyncCountdownEvent countdown, WorkInfo info) {
            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Starting: {Id}", w.Value.Id);
            Assert.Equal("Hello", w.Value.Data);

            try {
                // randomly complete, abandon or blowup.
                if (RandomData.GetBool()) {
                    if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Completing: {Id}", w.Value.Id);
                    await w.CompleteAsync();
                    info.IncrementCompletedCount();
                }
                else if (RandomData.GetBool()) {
                    if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Abandoning: {Id}", w.Value.Id);
                    await w.AbandonAsync();
                    info.IncrementAbandonCount();
                }
                else {
                    if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Erroring: {Id}", w.Value.Id);
                    info.IncrementErrorCount();
                    throw new Exception();
                }
            }
            finally {
                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Signal {CurrentCount}", countdown.CurrentCount);
                countdown.Signal();
            }
        }

        private async Task AssertEmptyQueueAsync(IQueue<SimpleWorkItem> queue) {
            var stats = await queue.GetQueueStatsAsync();
            Assert.Equal(0, stats.Abandoned);
            Assert.Equal(0, stats.Completed);
            Assert.Equal(0, stats.Deadletter);
            Assert.Equal(0, stats.Dequeued);
            Assert.Equal(0, stats.Enqueued);
            Assert.Equal(0, stats.Errors);
            Assert.Equal(0, stats.Queued);
            Assert.Equal(0, stats.Timeouts);
            Assert.Equal(0, stats.Working);
        }

        public virtual async Task MaintainJobNotAbandon_NotWorkTimeOutEntry() {
            var queue = GetQueue(retries: 0, workItemTimeout: TimeSpan.FromMilliseconds(100), retryDelay: TimeSpan.Zero);
            if (queue == null)
                return;
            try {
                await queue.DeleteQueueAsync();
                await AssertEmptyQueueAsync(queue);
                queue.EnqueueAsync(new SimpleWorkItem {
                    Data = "Hello World",
                    Id = 1
                });
                queue.EnqueueAsync(new SimpleWorkItem {
                    Data = "Hello World",
                    Id = 2
                });

                var dequeuedQueueItem = Assert.IsType<QueueEntry<SimpleWorkItem>>(await queue.DequeueAsync());
                Assert.NotNull(dequeuedQueueItem.Value);
                // The first dequeued item works for 60 milliseconds less than work timeout(100 milliseconds).
                await SystemClock.SleepAsync(60);
                await dequeuedQueueItem.CompleteAsync();
                Assert.True(dequeuedQueueItem.IsCompleted);
                Assert.False(dequeuedQueueItem.IsAbandoned);

                dequeuedQueueItem = Assert.IsType<QueueEntry<SimpleWorkItem>>(await queue.DequeueAsync());
                Assert.NotNull(dequeuedQueueItem.Value);
                // The second dequeued item works for 60 milliseconds less than work timeout(100 milliseconds).
                await SystemClock.SleepAsync(60);
                await dequeuedQueueItem.CompleteAsync();
                Assert.True(dequeuedQueueItem.IsCompleted);
                Assert.False(dequeuedQueueItem.IsAbandoned);

                var stats = await queue.GetQueueStatsAsync();
                Assert.Equal(0, stats.Working);
                Assert.Equal(0, stats.Abandoned);
                Assert.Equal(2, stats.Completed);
            }
            finally {
                await CleanupQueueAsync(queue);
            }
        }

        public virtual void Dispose() {
            var queue = GetQueue();
            if (queue == null)
                return;

            using (queue) {
                queue.DeleteQueueAsync().GetAwaiter().GetResult();
            }
        }
    }

    public class WorkInfo {
        private int _abandonCount = 0;
        private int _errorCount = 0;
        private int _completedCount = 0;

        public int AbandonCount => _abandonCount;
        public int ErrorCount => _errorCount;
        public int CompletedCount => _completedCount;

        public void IncrementAbandonCount() {
            Interlocked.Increment(ref _abandonCount);
        }

        public void IncrementErrorCount() {
            Interlocked.Increment(ref _errorCount);
        }

        public void IncrementCompletedCount() {
            Interlocked.Increment(ref _completedCount);
        }
    }
}