using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Exceptionless;
using Foundatio.AsyncEx;
using Foundatio.Caching;
using Foundatio.Jobs;
using Foundatio.Lock;
using Foundatio.Messaging;
using Foundatio.Queues;
using Foundatio.Tests.Extensions;
using Foundatio.Tests.Metrics;
using Foundatio.Utility;
using Foundatio.Xunit;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
#pragma warning disable CS4014

namespace Foundatio.Tests.Queue;

public abstract class QueueTestBase : TestWithLoggingBase, IDisposable
{
    protected QueueTestBase(ITestOutputHelper output) : base(output)
    {
        Log.SetLogLevel<InMemoryCacheClient>(LogLevel.Debug);
        Log.SetLogLevel<ScheduledTimer>(LogLevel.Debug);
    }

    protected virtual IQueue<SimpleWorkItem> GetQueue(int retries = 1, TimeSpan? workItemTimeout = null, TimeSpan? retryDelay = null, int[] retryMultipliers = null, int deadLetterMaxItems = 100, bool runQueueMaintenance = true, TimeProvider timeProvider = null)
    {
        return null;
    }

    protected virtual async Task CleanupQueueAsync(IQueue<SimpleWorkItem> queue)
    {
        if (queue == null)
            return;

        try
        {
            await queue.DeleteQueueAsync();
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Error))
                _logger.LogError(ex, "Error cleaning up queue");
        }
        finally
        {
            queue.Dispose();
        }
    }

    protected bool _assertStats = true;

    public virtual async Task CanQueueAndDequeueWorkItemAsync()
    {
        var queue = GetQueue();
        if (queue == null)
            return;

        using var metricsCollector = new DiagnosticsMetricsCollector(FoundatioDiagnostics.Meter.Name, _logger);

        try
        {
            await queue.DeleteQueueAsync();
            await AssertEmptyQueueAsync(queue);

            await queue.EnqueueAsync(new SimpleWorkItem
            {
                Data = "Hello",
                SubMetricName = "myitem"
            });
            Assert.Equal(1, (await queue.GetQueueStatsAsync()).Enqueued);

            var workItem = await queue.DequeueAsync(TimeSpan.Zero);
            Assert.NotNull(workItem);
            Assert.Equal("Hello", workItem.Value.Data);
            if (_assertStats)
                Assert.Equal(1, (await queue.GetQueueStatsAsync()).Dequeued);

            await workItem.CompleteAsync();
            Assert.False(workItem.IsAbandoned);
            Assert.True(workItem.IsCompleted);

            metricsCollector.RecordObservableInstruments();
            if (_assertStats)
            {
                var stats = await queue.GetQueueStatsAsync();
                Assert.Equal(1, stats.Completed);
                Assert.Equal(0, stats.Queued);

                Assert.Equal(1, metricsCollector.GetSum<long>("foundatio.simpleworkitem.enqueued"));
                Assert.Equal(1, metricsCollector.GetSum<long>("foundatio.simpleworkitem.dequeued"));
                Assert.Equal(1, metricsCollector.GetSum<long>("foundatio.simpleworkitem.completed"));

                Assert.Equal(0, metricsCollector.GetSum<long>("foundatio.simpleworkitem.count"));
                Assert.Equal(0, metricsCollector.GetSum<long>("foundatio.simpleworkitem.working"));
                Assert.Equal(0, metricsCollector.GetSum<long>("foundatio.simpleworkitem.deadletter"));

                Assert.Equal(1, metricsCollector.GetSum<long>("foundatio.simpleworkitem.myitem.enqueued"));
                Assert.Equal(1, metricsCollector.GetSum<long>("foundatio.simpleworkitem.myitem.dequeued"));
                Assert.Equal(1, metricsCollector.GetSum<long>("foundatio.simpleworkitem.myitem.completed"));
            }
        }
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    public virtual async Task CanQueueAndDequeueWorkItemWithDelayAsync()
    {
        var queue = GetQueue();
        if (queue == null)
            return;

        using var metricsCollector = new DiagnosticsMetricsCollector(FoundatioDiagnostics.Meter.Name, _logger);

        try
        {
            await queue.DeleteQueueAsync();
            await AssertEmptyQueueAsync(queue);

            await queue.EnqueueAsync(new SimpleWorkItem
            {
                Data = "Hello"
            }, new QueueEntryOptions { DeliveryDelay = TimeSpan.FromSeconds(1) });
            Assert.Equal(1, (await queue.GetQueueStatsAsync()).Enqueued);

            var workItem = await queue.DequeueAsync(TimeSpan.Zero);
            Assert.Null(workItem);

            workItem = await queue.DequeueAsync(TimeSpan.FromSeconds(2));
            Assert.NotNull(workItem);
            Assert.Equal("Hello", workItem.Value.Data);
            if (_assertStats)
                Assert.Equal(1, (await queue.GetQueueStatsAsync()).Dequeued);

            await workItem.CompleteAsync();
            Assert.False(workItem.IsAbandoned);
            Assert.True(workItem.IsCompleted);

            if (_assertStats)
            {
                var stats = await queue.GetQueueStatsAsync();
                Assert.Equal(1, stats.Completed);
                Assert.Equal(0, stats.Queued);

                metricsCollector.RecordObservableInstruments();
                Assert.Equal(1, metricsCollector.GetSum<long>("foundatio.simpleworkitem.enqueued"));
                Assert.Equal(1, metricsCollector.GetSum<long>("foundatio.simpleworkitem.dequeued"));
                Assert.Equal(1, metricsCollector.GetSum<long>("foundatio.simpleworkitem.completed"));

                Assert.Equal(0, metricsCollector.GetSum<long>("foundatio.simpleworkitem.count"));
                Assert.Equal(0, metricsCollector.GetSum<long>("foundatio.simpleworkitem.working"));
                Assert.Equal(0, metricsCollector.GetSum<long>("foundatio.simpleworkitem.deadletter"));
            }
        }
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    public virtual async Task CanUseQueueOptionsAsync()
    {
        var queue = GetQueue(retryDelay: TimeSpan.Zero);
        if (queue == null)
            return;

        using var metricsCollector = new DiagnosticsMetricsCollector(FoundatioDiagnostics.Meter.Name, _logger);

        try
        {
            using var listener = new ActivityListener
            {
                ShouldListenTo = s => s.Name == "Foundatio",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStarted = activity => _logger.LogInformation("Start: " + activity.DisplayName),
                ActivityStopped = activity => _logger.LogInformation("Stop: " + activity.DisplayName)
            };

            Activity.Current = new Activity("Parent");

            ActivitySource.AddActivityListener(listener);

            await queue.DeleteQueueAsync();
            await AssertEmptyQueueAsync(queue);

            await queue.EnqueueAsync(new SimpleWorkItem
            {
                Data = "Hello"
            }, new QueueEntryOptions
            {
                CorrelationId = "123+456",
                Properties = new Dictionary<string, string> {
                    { "hey", "now" }
                }
            });
            Assert.Equal(1, (await queue.GetQueueStatsAsync()).Enqueued);

            var workItem = await queue.DequeueAsync(TimeSpan.Zero);
            Assert.NotNull(workItem);
            Assert.Equal("Hello", workItem.Value.Data);
            Assert.Equal("123+456", workItem.CorrelationId);
            Assert.Single(workItem.Properties);
            Assert.Contains(workItem.Properties, i => i.Key == "hey" && i.Value.ToString() == "now");
            if (_assertStats)
            {
                Assert.Equal(1, (await queue.GetQueueStatsAsync()).Dequeued);
                Assert.Equal(1, metricsCollector.GetSum<long>("foundatio.simpleworkitem.dequeued"));
            }

            await workItem.AbandonAsync();
            Assert.True(workItem.IsAbandoned);
            Assert.False(workItem.IsCompleted);
            await Task.Delay(100);

            if (_assertStats)
            {
                var stats = await queue.GetQueueStatsAsync();
                Assert.Equal(1, stats.Abandoned);
                Assert.Equal(0, stats.Completed);
                Assert.Equal(1, stats.Queued);

                metricsCollector.RecordObservableInstruments();
                Assert.Equal(0, metricsCollector.GetSum<long>("foundatio.simpleworkitem.completed"));
                Assert.Equal(1, metricsCollector.GetSum<long>("foundatio.simpleworkitem.abandoned"));
                Assert.Equal(1, metricsCollector.GetMax<long>("foundatio.simpleworkitem.count"));
            }

            workItem = await queue.DequeueAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(workItem);
            Assert.Equal("Hello", workItem.Value.Data);
            Assert.Equal("123+456", workItem.CorrelationId);
            Assert.Equal(2, workItem.Attempts);
            Assert.Single(workItem.Properties);
            Assert.Contains(workItem.Properties, i => i.Key == "hey" && i.Value.ToString() == "now");
        }
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    public virtual async Task CanDiscardDuplicateQueueEntriesAsync()
    {
        var queue = GetQueue();
        if (queue == null)
            return;

        using var metricsCollector = new DiagnosticsMetricsCollector(FoundatioDiagnostics.Meter.Name, _logger);

        try
        {
            await queue.DeleteQueueAsync();
            await AssertEmptyQueueAsync(queue);
            queue.AttachBehavior(new DuplicateDetectionQueueBehavior<SimpleWorkItem>(new InMemoryCacheClient(o => o.LoggerFactory(Log)), Log));

            await queue.EnqueueAsync(new SimpleWorkItem
            {
                Data = "Hello",
                UniqueIdentifier = "123"
            });
            if (_assertStats)
            {
                Assert.Equal(1, (await queue.GetQueueStatsAsync()).Enqueued);
                Assert.Equal(1, metricsCollector.GetSum<long>("foundatio.simpleworkitem.enqueued"));
            }

            await queue.EnqueueAsync(new SimpleWorkItem
            {
                Data = "Hello",
                UniqueIdentifier = "123"
            });
            if (_assertStats)
            {
                Assert.Equal(1, (await queue.GetQueueStatsAsync()).Enqueued);
                Assert.Equal(1, metricsCollector.GetSum<long>("foundatio.simpleworkitem.enqueued"));
            }

            var workItem = await queue.DequeueAsync(TimeSpan.Zero);
            Assert.NotNull(workItem);
            Assert.Equal("Hello", workItem.Value.Data);
            if (_assertStats)
            {
                Assert.Equal(1, (await queue.GetQueueStatsAsync()).Dequeued);
                Assert.Equal(1, metricsCollector.GetSum<long>("foundatio.simpleworkitem.dequeued"));
            }

            await queue.EnqueueAsync(new SimpleWorkItem
            {
                Data = "Hello",
                UniqueIdentifier = "123"
            });
            if (_assertStats)
            {
                Assert.Equal(2, (await queue.GetQueueStatsAsync()).Enqueued);
                Assert.Equal(2, metricsCollector.GetSum<long>("foundatio.simpleworkitem.enqueued"));
            }

            await workItem.CompleteAsync();
            Assert.False(workItem.IsAbandoned);
            Assert.True(workItem.IsCompleted);
            var stats = await queue.GetQueueStatsAsync();
            if (_assertStats)
            {
                Assert.Equal(1, stats.Completed);
                Assert.Equal(1, stats.Queued);

                metricsCollector.RecordObservableInstruments();
                Assert.Equal(2, metricsCollector.GetSum<long>("foundatio.simpleworkitem.enqueued"));
                Assert.Equal(1, metricsCollector.GetSum<long>("foundatio.simpleworkitem.dequeued"));
                Assert.Equal(1, metricsCollector.GetSum<long>("foundatio.simpleworkitem.completed"));
                Assert.Equal(0, metricsCollector.GetSum<long>("foundatio.simpleworkitem.abandoned"));

                Assert.Equal(1, metricsCollector.GetSum<long>("foundatio.simpleworkitem.count"));
                Assert.Equal(0, metricsCollector.GetSum<long>("foundatio.simpleworkitem.working"));
                Assert.Equal(0, metricsCollector.GetSum<long>("foundatio.simpleworkitem.deadletter"));
            }

        }
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    public virtual Task VerifyRetryAttemptsAsync()
    {
        const int retryCount = 2;
        var queue = GetQueue(retryCount, TimeSpan.FromSeconds(1), TimeSpan.Zero, [1]);
        if (queue == null)
            return Task.CompletedTask;

        return VerifyRetryAttemptsImplAsync(queue, retryCount, TimeSpan.FromSeconds(10));
    }

    public virtual Task VerifyDelayedRetryAttemptsAsync()
    {
        const int retryCount = 2;
        var queue = GetQueue(retryCount, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), [1]);
        if (queue == null)
            return Task.CompletedTask;

        return VerifyRetryAttemptsImplAsync(queue, retryCount, TimeSpan.FromSeconds(30));
    }

    private async Task VerifyRetryAttemptsImplAsync(IQueue<SimpleWorkItem> queue, int retryCount, TimeSpan waitTime)
    {
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        try
        {
            await queue.DeleteQueueAsync();
            await AssertEmptyQueueAsync(queue);

            using var metricsCollector = new DiagnosticsMetricsCollector(FoundatioDiagnostics.Meter.Name, _logger);

            var countdown = new AsyncCountdownEvent(retryCount + 1);
            int attempts = 0;

            await queue.StartWorkingAsync(async w =>
            {
                Interlocked.Increment(ref attempts);
                _logger.LogInformation("Starting Attempt {Attempt} to work on queue item", attempts);
                Assert.Equal("Hello", w.Value.Data);

                var queueEntryMetadata = (IQueueEntryMetadata)w;
                Assert.Equal(attempts, queueEntryMetadata.Attempts);

                await w.AbandonAsync();
                countdown.Signal();

                _logger.LogInformation("Finished Attempt {Attempt} to work on queue item, Metadata Attempts: {MetadataAttempts}", attempts, queueEntryMetadata.Attempts);
            }, cancellationToken: cancellationTokenSource.Token);

            await queue.EnqueueAsync(new SimpleWorkItem
            {
                Data = "Hello"
            });

            await countdown.WaitAsync(waitTime);
            Assert.Equal(0, countdown.CurrentCount);

            if (_assertStats)
            {
                var stats = await queue.GetQueueStatsAsync();
                Assert.Equal(retryCount + 1, attempts);
                Assert.Equal(0, stats.Completed);
                Assert.Equal(0, stats.Queued);
                Assert.Equal(0, stats.Errors);
                Assert.Equal(retryCount + 1, stats.Dequeued);
                Assert.Equal(retryCount + 1, stats.Abandoned);

                metricsCollector.RecordObservableInstruments();
                Assert.Equal(retryCount + 1, metricsCollector.GetSum<long>("foundatio.simpleworkitem.dequeued"));
                Assert.Equal(0, metricsCollector.GetSum<long>("foundatio.simpleworkitem.completed"));
                Assert.Equal(retryCount + 1, metricsCollector.GetSum<long>("foundatio.simpleworkitem.abandoned"));

                Assert.Equal(0, metricsCollector.GetSum<long>("foundatio.simpleworkitem.count"));
            }
        }
        finally
        {
            await cancellationTokenSource.CancelAsync();
            await CleanupQueueAsync(queue);
        }
    }

    /// <summary>
    /// When a cancelled token is passed into Dequeue, it will only try to dequeue one time and then exit.
    /// </summary>
    /// <returns></returns>
    public virtual async Task CanDequeueWithCancelledTokenAsync()
    {
        var queue = GetQueue();
        if (queue == null)
            return;

        using var metricsCollector = new DiagnosticsMetricsCollector(FoundatioDiagnostics.Meter.Name, _logger);

        try
        {
            await queue.DeleteQueueAsync();
            await AssertEmptyQueueAsync(queue);

            await queue.EnqueueAsync(new SimpleWorkItem
            {
                Data = "Hello"
            });
            if (_assertStats)
            {
                Assert.Equal(1, (await queue.GetQueueStatsAsync()).Enqueued);
                Assert.Equal(1, metricsCollector.GetSum<long>("foundatio.simpleworkitem.enqueued"));
            }

            var workItem = await queue.DequeueAsync(new CancellationToken(true));
            Assert.NotNull(workItem);
            Assert.Equal("Hello", workItem.Value.Data);
            Assert.Equal(1, (await queue.GetQueueStatsAsync()).Dequeued);

            // TODO: We should verify that only one retry occurred.
            await workItem.CompleteAsync();

            if (_assertStats)
            {
                var stats = await queue.GetQueueStatsAsync();
                Assert.Equal(1, stats.Completed);
                Assert.Equal(0, stats.Queued);
                Assert.Equal(1, metricsCollector.GetSum<long>("foundatio.simpleworkitem.completed"));
            }
        }
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    public virtual async Task CanDequeueEfficientlyAsync()
    {
        const int iterations = 100;

        var queue = GetQueue(runQueueMaintenance: false);
        if (queue == null)
            return;

        try
        {
            await queue.DeleteQueueAsync();
            await AssertEmptyQueueAsync(queue);
            await queue.EnqueueAsync(new SimpleWorkItem { Data = "Initialize queue to create more accurate metrics" });
            Assert.NotNull(await queue.DequeueAsync(TimeSpan.FromSeconds(1)));

            using var metricsCollector = new DiagnosticsMetricsCollector(FoundatioDiagnostics.Meter.Name, _logger);

            _ = Task.Run(async () =>
            {
                _logger.LogTrace("Starting enqueue loop");
                for (int index = 0; index < iterations; index++)
                {
                    await Task.Delay(RandomData.GetInt(10, 30));
                    await queue.EnqueueAsync(new SimpleWorkItem { Data = "Hello" });
                }
                _logger.LogTrace("Finished enqueuing");
            });

            _logger.LogTrace("Starting dequeue loop");
            for (int index = 0; index < iterations; index++)
            {
                var item = await queue.DequeueAsync(TimeSpan.FromSeconds(3));
                Assert.NotNull(item);
                await item.CompleteAsync();
            }
            _logger.LogTrace("Finished dequeuing");

            Assert.InRange(metricsCollector.GetAvg<double>("foundatio.simpleworkitem.queuetime"), 0, 75);
        }
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    public virtual async Task CanResumeDequeueEfficientlyAsync()
    {
        const int iterations = 10;

        var queue = GetQueue(runQueueMaintenance: false);
        if (queue == null)
            return;

        try
        {
            using var metricsCollector = new DiagnosticsMetricsCollector(FoundatioDiagnostics.Meter.Name, _logger);

            await queue.DeleteQueueAsync();
            await AssertEmptyQueueAsync(queue);

            for (int index = 0; index < iterations; index++)
                await queue.EnqueueAsync(new SimpleWorkItem { Data = "Hello" });

            using var secondQueue = GetQueue(runQueueMaintenance: false);

            _logger.LogTrace("Starting dequeue loop");
            for (int index = 0; index < iterations; index++)
            {
                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("[{Index}] Calling Dequeue", index);
                var item = await secondQueue.DequeueAsync(TimeSpan.FromSeconds(3));
                Assert.NotNull(item);
                await item.CompleteAsync();
            }

            metricsCollector.RecordObservableInstruments();
            Assert.InRange(metricsCollector.GetAvg<double>("foundatio.simpleworkitem.queuetime"), 0, 75);
        }
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    public virtual async Task CanQueueAndDequeueMultipleWorkItemsAsync()
    {
        var queue = GetQueue();
        if (queue == null)
            return;

        try
        {
            using var metricsCollector = new DiagnosticsMetricsCollector(FoundatioDiagnostics.Meter.Name, _logger);

            await queue.DeleteQueueAsync();
            await AssertEmptyQueueAsync(queue);

            const int workItemCount = 25;
            for (int i = 0; i < workItemCount; i++)
            {
                await queue.EnqueueAsync(new SimpleWorkItem
                {
                    Data = "Hello"
                });
            }
            metricsCollector.RecordObservableInstruments();
            Assert.Equal(workItemCount, metricsCollector.GetMax<long>("foundatio.simpleworkitem.count"));
            Assert.Equal(workItemCount, (await queue.GetQueueStatsAsync()).Queued);

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < workItemCount; i++)
            {
                var workItem = await queue.DequeueAsync(TimeSpan.FromSeconds(5));
                Assert.NotNull(workItem);
                Assert.Equal("Hello", workItem.Value.Data);
                await workItem.CompleteAsync();
            }
            sw.Stop();
            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Time {Elapsed:g}", sw.Elapsed);
            Assert.InRange(sw.Elapsed.TotalSeconds, 0, 5);

            if (_assertStats)
            {
                var stats = await queue.GetQueueStatsAsync();
                Assert.Equal(workItemCount, stats.Dequeued);
                Assert.Equal(workItemCount, stats.Completed);
                Assert.Equal(0, stats.Queued);
            }
        }
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    public virtual async Task WillNotWaitForItemAsync()
    {
        var queue = GetQueue();
        if (queue == null)
            return;

        try
        {
            await queue.DeleteQueueAsync();
            await AssertEmptyQueueAsync(queue);

            var sw = Stopwatch.StartNew();
            var workItem = await queue.DequeueAsync(TimeSpan.Zero);
            sw.Stop();
            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Time {Elapsed:g}", sw.Elapsed);
            Assert.Null(workItem);
            Assert.InRange(sw.Elapsed.TotalMilliseconds, 0, 100);
        }
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    public virtual async Task WillWaitForItemAsync()
    {
        var queue = GetQueue();
        if (queue == null)
            return;

        try
        {
            await queue.DeleteQueueAsync();
            await AssertEmptyQueueAsync(queue);

            var sw = Stopwatch.StartNew();
            var workItem = await queue.DequeueAsync(TimeSpan.FromMilliseconds(100));
            sw.Stop();
            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Time {Elapsed:g}", sw.Elapsed);
            Assert.Null(workItem);
            Assert.InRange(sw.Elapsed, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(5000));

            _ = Task.Run(async () =>
            {
                await Task.Delay(500);
                await queue.EnqueueAsync(new SimpleWorkItem
                {
                    Data = "Hello"
                });
            });

            sw.Restart();
            workItem = await queue.DequeueAsync(TimeSpan.FromSeconds(10));
            sw.Stop();
            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Time {Elapsed:g}", sw.Elapsed);
            Assert.True(sw.Elapsed > TimeSpan.FromMilliseconds(400));
            Assert.NotNull(workItem);
            await workItem.CompleteAsync();

        }
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    public virtual async Task DequeueWaitWillGetSignaledAsync()
    {
        var queue = GetQueue();
        if (queue == null)
            return;

        try
        {
            await queue.DeleteQueueAsync();
            await AssertEmptyQueueAsync(queue);

            _ = Task.Run(async () =>
            {
                await Task.Delay(250);
                await queue.EnqueueAsync(new SimpleWorkItem
                {
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
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    public virtual async Task CanUseQueueWorkerAsync()
    {
        var queue = GetQueue();
        if (queue == null)
            return;

        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await queue.DeleteQueueAsync();
            await AssertEmptyQueueAsync(queue);

            var resetEvent = new AsyncManualResetEvent(false);
            await queue.StartWorkingAsync(async w =>
            {
                Assert.Equal("Hello", w.Value.Data);
                await w.CompleteAsync();
                resetEvent.Set();
            }, cancellationToken: cancellationTokenSource.Token);

            await queue.EnqueueAsync(new SimpleWorkItem
            {
                Data = "Hello"
            });

            await resetEvent.WaitAsync();
            if (_assertStats)
            {
                var stats = await queue.GetQueueStatsAsync();
                Assert.Equal(1, stats.Completed);
                Assert.Equal(0, stats.Queued);
                Assert.Equal(0, stats.Errors);
            }
        }
        finally
        {
            await cancellationTokenSource.CancelAsync();
            await CleanupQueueAsync(queue);
        }
    }

    public virtual async Task CanHandleErrorInWorkerAsync()
    {
        var queue = GetQueue(retries: 0);
        if (queue == null)
            return;

        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await queue.DeleteQueueAsync();
            await AssertEmptyQueueAsync(queue);

            await queue.StartWorkingAsync(w =>
            {
                _logger.LogDebug("WorkAction");
                Assert.Equal("Hello", w.Value.Data);
                throw new Exception();
            }, cancellationToken: cancellationTokenSource.Token);

            var resetEvent = new AsyncManualResetEvent(false);
            using (queue.Abandoned.AddSyncHandler((o, args) => resetEvent.Set()))
            {
                await queue.EnqueueAsync(new SimpleWorkItem { Data = "Hello" });
                await resetEvent.WaitAsync(TimeSpan.FromSeconds(200));

                await Task.Delay(100); // give time for the stats to reflect the changes.

                if (_assertStats)
                {
                    var stats = await queue.GetQueueStatsAsync();
                    if (_logger.IsEnabled(LogLevel.Information))
                        _logger.LogInformation("Completed: {Completed} Errors: {Errors} Deadletter: {Deadletter} Working: {Working} ", stats.Completed, stats.Errors, stats.Deadletter, stats.Working);
                    Assert.Equal(0, stats.Completed);
                    Assert.Equal(1, stats.Errors);
                    Assert.Equal(1, stats.Deadletter);
                }
            }
        }
        finally
        {
            await cancellationTokenSource.CancelAsync();
            await CleanupQueueAsync(queue);
        }
    }

    public virtual async Task WorkItemsWillTimeoutAsync()
    {
        Log.SetLogLevel("Foundatio.Queues.RedisQueue", LogLevel.Trace);
        var queue = GetQueue(retryDelay: TimeSpan.Zero, workItemTimeout: TimeSpan.FromMilliseconds(50));
        if (queue == null)
            return;

        try
        {
            await queue.DeleteQueueAsync();
            await AssertEmptyQueueAsync(queue);

            await queue.EnqueueAsync(new SimpleWorkItem
            {
                Data = "Hello"
            });
            var workItem = await queue.DequeueAsync(TimeSpan.Zero);
            Assert.NotNull(workItem);
            Assert.Equal("Hello", workItem.Value.Data);

            var sw = Stopwatch.StartNew();
            if (_assertStats)
            {
                // wait for the entry to be auto abandoned
                do
                {
                    var stats = await queue.GetQueueStatsAsync();
                    if (stats.Abandoned > 0)
                        break;
                    await Task.Delay(250);
                } while (sw.Elapsed < TimeSpan.FromSeconds(10));
            }

            // should throw because the item has already been auto abandoned
            if (_assertStats)
                await Assert.ThrowsAnyAsync<Exception>(async () => await workItem.CompleteAsync().AnyContext());

            sw = Stopwatch.StartNew();
            workItem = await queue.DequeueAsync(TimeSpan.FromSeconds(5));
            sw.Stop();
            _logger.LogTrace("Time {Elapsed:g}", sw.Elapsed);
            Assert.NotNull(workItem);
            await workItem.CompleteAsync();
            if (_assertStats)
                Assert.Equal(0, (await queue.GetQueueStatsAsync()).Queued);
        }
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    public virtual async Task WorkItemsWillGetMovedToDeadletterAsync()
    {
        var queue = GetQueue(retryDelay: TimeSpan.Zero);
        if (queue == null)
            return;

        try
        {
            await queue.DeleteQueueAsync();
            await AssertEmptyQueueAsync(queue);

            await queue.EnqueueAsync(new SimpleWorkItem
            {
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

            if (_assertStats)
            {
                // work item should be moved to deadletter _queue after retries.
                var stats = await queue.GetQueueStatsAsync();
                Assert.Equal(1, stats.Deadletter);
                Assert.Equal(2, stats.Abandoned);
            }
        }
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    public virtual async Task CanAutoCompleteWorkerAsync()
    {
        var queue = GetQueue();
        if (queue == null)
            return;

        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await queue.DeleteQueueAsync();
            await AssertEmptyQueueAsync(queue);

            var resetEvent = new AsyncManualResetEvent(false);
            await queue.StartWorkingAsync(w =>
            {
                Assert.Equal("Hello", w.Value.Data);
                return Task.CompletedTask;
            }, true, cancellationTokenSource.Token);

            using (queue.Completed.AddSyncHandler((s, e) => { resetEvent.Set(); }))
            {
                await queue.EnqueueAsync(new SimpleWorkItem { Data = "Hello" });

                Assert.Equal(1, (await queue.GetQueueStatsAsync()).Enqueued);
                await resetEvent.WaitAsync(TimeSpan.FromSeconds(2));

                if (_assertStats)
                {
                    var stats = await queue.GetQueueStatsAsync();
                    Assert.Equal(0, stats.Queued);
                    Assert.Equal(0, stats.Errors);
                    Assert.Equal(1, stats.Completed);
                }
            }
        }
        finally
        {
            await cancellationTokenSource.CancelAsync();
            await CleanupQueueAsync(queue);
        }
    }

    public virtual async Task CanHaveMultipleQueueInstancesAsync()
    {
        var queue = GetQueue(retries: 0, retryDelay: TimeSpan.Zero);
        if (queue == null)
            return;

        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await queue.DeleteQueueAsync();
            await AssertEmptyQueueAsync(queue);

            const int workItemCount = 500;
            const int workerCount = 3;
            var countdown = new AsyncCountdownEvent(workItemCount);
            var info = new WorkInfo();
            var workers = new List<IQueue<SimpleWorkItem>> { queue };

            try
            {
                for (int i = 0; i < workerCount; i++)
                {
                    var q = GetQueue(retries: 0, retryDelay: TimeSpan.Zero);
                    if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Queue Id: {Id}, I: {Instance}", q.QueueId, i);
                    await q.StartWorkingAsync(w => DoWorkAsync(w, countdown, info), cancellationToken: cancellationTokenSource.Token);
                    workers.Add(q);
                }

                await Parallel.ForEachAsync(Enumerable.Range(1, workItemCount), cancellationTokenSource.Token, async (i, _) =>
                {
                    string id = await queue.EnqueueAsync(new SimpleWorkItem
                    {
                        Data = "Hello",
                        Id = i
                    });
                    if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Enqueued Index: {Instance} Id: {Id}", i, id);
                });

                await countdown.WaitAsync(cancellationTokenSource.Token);
                await Task.Delay(50, cancellationTokenSource.Token);

                if (_logger.IsEnabled(LogLevel.Information))
                    _logger.LogInformation("Work Info Stats: Completed: {Completed} Abandoned: {Abandoned} Error: {Errors}", info.CompletedCount, info.AbandonCount, info.ErrorCount);
                Assert.Equal(workItemCount, info.CompletedCount + info.AbandonCount + info.ErrorCount);

                // In memory queue doesn't share state.
                if (queue.GetType() == typeof(InMemoryQueue<SimpleWorkItem>))
                {
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
                else if (_assertStats)
                {
                    var workerStats = new List<QueueStats>();
                    for (int i = 0; i < workers.Count; i++)
                    {
                        var stats = await workers[i].GetQueueStatsAsync();
                        if (_logger.IsEnabled(LogLevel.Information))
                            _logger.LogInformation("Worker#{Id} Working: {Working} Completed: {Completed} Abandoned: {Abandoned} Error: {Errors} Deadletter: {Deadletter}", i, stats.Working, stats.Completed, stats.Abandoned, stats.Errors, stats.Deadletter);
                        workerStats.Add(stats);
                    }

                    Assert.Equal(info.CompletedCount, workerStats.Sum(s => s.Completed));
                    Assert.Equal(info.ErrorCount, workerStats.Sum(s => s.Errors));
                    Assert.Equal(info.AbandonCount, workerStats.Sum(s => s.Abandoned) - info.ErrorCount);
                    Assert.Equal(info.AbandonCount + workerStats.Sum(s => s.Errors), (workerStats.LastOrDefault()?.Deadletter ?? 0));
                    //Expected: 260
                    //Actual:   125
                }
            }
            finally
            {
                await cancellationTokenSource.CancelAsync();
                foreach (var q in workers)
                    await CleanupQueueAsync(q);
            }
        }
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    public virtual async Task CanDelayRetryAsync()
    {
        var queue = GetQueue(workItemTimeout: TimeSpan.FromMilliseconds(500), retryDelay: TimeSpan.FromSeconds(1));
        if (queue == null)
            return;

        try
        {
            await queue.DeleteQueueAsync();
            await AssertEmptyQueueAsync(queue);

            await queue.EnqueueAsync(new SimpleWorkItem
            {
                Data = "Hello"
            });

            var workItem = await queue.DequeueAsync(TimeSpan.Zero);
            Assert.NotNull(workItem);
            Assert.Equal("Hello", workItem.Value.Data);

            var startTime = DateTime.UtcNow;
            await workItem.AbandonAsync();
            Assert.Equal(1, (await queue.GetQueueStatsAsync()).Abandoned);

            workItem = await queue.DequeueAsync(TimeSpan.FromSeconds(5));
            var elapsed = DateTime.UtcNow.Subtract(startTime);
            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Time {Elapsed}", elapsed);
            Assert.NotNull(workItem);
            Assert.True(elapsed > TimeSpan.FromSeconds(.95));
            await workItem.CompleteAsync();

            if (_assertStats)
                Assert.Equal(0, (await queue.GetQueueStatsAsync()).Queued);
        }
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    public virtual async Task CanRunWorkItemWithMetricsAsync()
    {
        int completedCount = 0;

        using var queue = new InMemoryQueue<WorkItemData>(o => o.LoggerFactory(Log));

        Task Handler(object sender, CompletedEventArgs<WorkItemData> e)
        {
            completedCount++;
            return Task.CompletedTask;
        }

        using var metricsCollector = new DiagnosticsMetricsCollector(FoundatioDiagnostics.Meter.Name, _logger);

        using (queue.Completed.AddHandler(Handler))
        {
            _logger.LogTrace("Before enqueue");
            await queue.EnqueueAsync(new SimpleWorkItem { Id = 1, Data = "Testing" });
            await queue.EnqueueAsync(new SimpleWorkItem { Id = 2, Data = "Testing" });
            await queue.EnqueueAsync(new SimpleWorkItem { Id = 3, Data = "Testing" });

            await Task.Delay(100);

            _logger.LogTrace("Before dequeue");
            var item = await queue.DequeueAsync();
            await Task.Delay(100);
            await item.CompleteAsync();

            item = await queue.DequeueAsync();
            await Task.Delay(100);
            await item.CompleteAsync();

            item = await queue.DequeueAsync();
            await Task.Delay(100);
            await item.AbandonAsync();

            _logger.LogTrace("Before asserts");
            Assert.Equal(2, completedCount);

            metricsCollector.RecordObservableInstruments();
            Assert.InRange(metricsCollector.GetMax<long>("foundatio.workitemdata.count"), 1, 3);
            Assert.InRange(metricsCollector.GetMax<long>("foundatio.workitemdata.working"), 0, 1);

            Assert.Equal(3, metricsCollector.GetCount<long>("foundatio.workitemdata.simple.enqueued"));
            Assert.Equal(3, metricsCollector.GetCount<long>("foundatio.workitemdata.enqueued"));

            Assert.Equal(3, metricsCollector.GetCount<long>("foundatio.workitemdata.simple.dequeued"));
            Assert.Equal(3, metricsCollector.GetCount<long>("foundatio.workitemdata.dequeued"));

            Assert.Equal(2, metricsCollector.GetCount<long>("foundatio.workitemdata.simple.completed"));
            Assert.Equal(2, metricsCollector.GetCount<long>("foundatio.workitemdata.completed"));

            Assert.Equal(1, metricsCollector.GetCount<long>("foundatio.workitemdata.simple.abandoned"));
            Assert.Equal(1, metricsCollector.GetCount<long>("foundatio.workitemdata.abandoned"));

            var measurements = metricsCollector.GetMeasurements<double>("foundatio.workitemdata.simple.queuetime");
            Assert.Equal(3, measurements.Count);
            measurements = metricsCollector.GetMeasurements<double>("foundatio.workitemdata.queuetime");
            Assert.Equal(3, measurements.Count);

            measurements = metricsCollector.GetMeasurements<double>("foundatio.workitemdata.simple.processtime");
            Assert.Equal(3, measurements.Count);
            measurements = metricsCollector.GetMeasurements<double>("foundatio.workitemdata.processtime");
            Assert.Equal(3, measurements.Count);
        }
    }

    public virtual async Task CanRenewLockAsync()
    {
        Log.SetLogLevel<InMemoryQueue<SimpleWorkItem>>(LogLevel.Trace);

        // Need large value to reproduce this test
        var workItemTimeout = TimeSpan.FromSeconds(1);
        // Slightly shorter than the timeout to ensure we haven't lost the lock
        var renewWait = TimeSpan.FromSeconds(workItemTimeout.TotalSeconds * .25d);

        var queue = GetQueue(retryDelay: TimeSpan.Zero, workItemTimeout: workItemTimeout);
        if (queue == null)
            return;

        try
        {
            await queue.DeleteQueueAsync();
            await AssertEmptyQueueAsync(queue);

            await queue.EnqueueAsync(new SimpleWorkItem
            {
                Data = "Hello"
            });
            var entry = await queue.DequeueAsync(TimeSpan.Zero);
            Assert.NotNull(entry);
            Assert.Equal("Hello", entry.Value.Data);

            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Waiting for {RenewWait:g} before renewing lock", renewWait);
            await Task.Delay(renewWait);
            _logger.LogTrace("Renewing lock");
            await entry.RenewLockAsync();
            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Waiting for {RenewWait:g} to see if lock was renewed", renewWait);
            await Task.Delay(renewWait);

            // We shouldn't get another item here if RenewLock works.
            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Attempting to dequeue item that shouldn't exist");
            var nullWorkItem = await queue.DequeueAsync(TimeSpan.Zero);
            Assert.Null(nullWorkItem);
            await entry.CompleteAsync();

            if (_assertStats)
                Assert.Equal(0, (await queue.GetQueueStatsAsync()).Queued);
        }
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    public virtual async Task CanAbandonQueueEntryOnceAsync()
    {
        var queue = GetQueue();
        if (queue == null)
            return;

        try
        {
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

            if (_assertStats)
            {
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
            }

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
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    public virtual async Task CanCompleteQueueEntryOnceAsync()
    {
        var queue = GetQueue();
        if (queue == null)
            return;

        try
        {
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

            if (_assertStats)
            {
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
            }

            if (workItem is QueueEntry<SimpleWorkItem> queueEntry)
                Assert.Equal(1, queueEntry.Attempts);
        }
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    public virtual async Task CanDequeueWithLockingAsync()
    {
        using var cache = new InMemoryCacheClient(o => o.LoggerFactory(Log));
        using var messageBus = new InMemoryMessageBus(o => o.LoggerFactory(Log));

        var distributedLock = new CacheLockProvider(cache, messageBus, null, Log);
        await CanDequeueWithLockingImpAsync(distributedLock);
    }

    protected async Task CanDequeueWithLockingImpAsync(CacheLockProvider distributedLock)
    {
        var queue = GetQueue(retryDelay: TimeSpan.Zero, retries: 0);
        if (queue == null)
            return;

        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await queue.DeleteQueueAsync();
            await AssertEmptyQueueAsync(queue);

            var resetEvent = new AsyncAutoResetEvent();
            await queue.StartWorkingAsync(async w =>
            {
                _logger.LogInformation("Acquiring distributed lock in work item");
                var l = await distributedLock.AcquireAsync("test");
                Assert.NotNull(l);
                _logger.LogInformation("Acquired distributed lock");
                await Task.Delay(TimeSpan.FromMilliseconds(250));
                await l.ReleaseAsync();
                _logger.LogInformation("Released distributed lock");

                await w.CompleteAsync();
                resetEvent.Set();
            }, cancellationToken: cancellationTokenSource.Token);

            await queue.EnqueueAsync(new SimpleWorkItem { Data = "Hello" });
            await resetEvent.WaitAsync(TimeSpan.FromSeconds(5));

            if (_assertStats)
            {
                await Task.Delay(1);
                var stats = await queue.GetQueueStatsAsync();
                _logger.LogInformation("Completed: {Completed} Errors: {Errors} Deadletter: {Deadletter} Working: {Working} ", stats.Completed, stats.Errors, stats.Deadletter, stats.Working);
                Assert.Equal(1, stats.Completed);
            }
        }
        finally
        {
            await cancellationTokenSource.CancelAsync();
            await CleanupQueueAsync(queue);
        }
    }

    public virtual async Task CanHaveMultipleQueueInstancesWithLockingAsync()
    {
        using var cache = new InMemoryCacheClient(o => o.LoggerFactory(Log));
        using var messageBus = new InMemoryMessageBus(o => o.LoggerFactory(Log));

        var distributedLock = new CacheLockProvider(cache, messageBus, null, Log);
        await CanHaveMultipleQueueInstancesWithLockingImplAsync(distributedLock);
    }

    protected async Task CanHaveMultipleQueueInstancesWithLockingImplAsync(CacheLockProvider distributedLock)
    {
        var queue = GetQueue(retries: 0, retryDelay: TimeSpan.Zero);
        if (queue == null)
            return;

        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await queue.DeleteQueueAsync();
            await AssertEmptyQueueAsync(queue);

            const int workItemCount = 16;
            const int workerCount = 4;
            var countdown = new AsyncCountdownEvent(workItemCount);
            var info = new WorkInfo();
            var workers = new List<IQueue<SimpleWorkItem>> { queue };

            try
            {
                for (int i = 0; i < workerCount; i++)
                {
                    var q = GetQueue(retries: 0, retryDelay: TimeSpan.Zero);
                    int instanceCount = i;
                    await q.StartWorkingAsync(async w =>
                    {
                        if (_logger.IsEnabled(LogLevel.Information))
                            _logger.LogInformation("[{Instance}] Acquiring distributed lock in work item: {Id}", instanceCount, w.Id);
                        var l = await distributedLock.AcquireAsync("test", cancellationToken: cancellationTokenSource.Token);
                        Assert.NotNull(l);
                        if (_logger.IsEnabled(LogLevel.Information))
                            _logger.LogInformation("[{Instance}] Acquired distributed lock: {Id}", instanceCount, w.Id);
                        await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationTokenSource.Token);
                        await l.ReleaseAsync();
                        if (_logger.IsEnabled(LogLevel.Information))
                            _logger.LogInformation("[{Instance}] Released distributed lock: {Id}", instanceCount, w.Id);

                        await w.CompleteAsync();
                        info.IncrementCompletedCount();
                        countdown.Signal();
                        if (_logger.IsEnabled(LogLevel.Information))
                            _logger.LogInformation("[{Instance}] Signaled countdown: {Id}", instanceCount, w.Id);
                    }, cancellationToken: cancellationTokenSource.Token);
                    workers.Add(q);
                }

                await Parallel.ForEachAsync(Enumerable.Range(1, workItemCount), cancellationTokenSource.Token, async (i, _) =>
                {
                    string id = await queue.EnqueueAsync(new SimpleWorkItem
                    {
                        Data = "Hello",
                        Id = i
                    });
                    if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Enqueued Index: {Instance} Id: {Id}", i, id);
                });

                await countdown.WaitAsync(TimeSpan.FromSeconds(5));
                await Task.Delay(50, cancellationTokenSource.Token);
                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Completed: {Completed} Abandoned: {Abandoned} Error: {Errors}", info.CompletedCount, info.AbandonCount, info.ErrorCount);

                if (_logger.IsEnabled(LogLevel.Information))
                    _logger.LogInformation("Work Info Stats: Completed: {Completed} Abandoned: {Abandoned} Error: {Errors}", info.CompletedCount, info.AbandonCount, info.ErrorCount);
                Assert.Equal(workItemCount, info.CompletedCount + info.AbandonCount + info.ErrorCount);

                // In memory queue doesn't share state.
                if (queue.GetType() == typeof(InMemoryQueue<SimpleWorkItem>))
                {
                    var stats = await queue.GetQueueStatsAsync();
                    Assert.Equal(info.CompletedCount, stats.Completed);
                }
                else
                {
                    var workerStats = new List<QueueStats>();
                    for (int i = 0; i < workers.Count; i++)
                    {
                        var stats = await workers[i].GetQueueStatsAsync();
                        if (_logger.IsEnabled(LogLevel.Information))
                            _logger.LogInformation("Worker#{Id} Working: {Working} Completed: {Completed} Abandoned: {Abandoned} Error: {Errors} Deadletter: {Deadletter}", i, stats.Working, stats.Completed, stats.Abandoned, stats.Errors, stats.Deadletter);
                        workerStats.Add(stats);
                    }

                    Assert.Equal(info.CompletedCount, workerStats.Sum(s => s.Completed));
                }
            }
            finally
            {
                await cancellationTokenSource.CancelAsync();
                foreach (var q in workers)
                    await CleanupQueueAsync(q);
            }
        }
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    protected async Task DoWorkAsync(IQueueEntry<SimpleWorkItem> w, AsyncCountdownEvent countdown, WorkInfo info)
    {
        if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Starting: {Id}", w.Value.Id);
        Assert.Equal("Hello", w.Value.Data);

        try
        {
            // randomly complete, abandon or blowup.
            if (RandomData.GetBool())
            {
                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Completing: {Id}", w.Value.Id);
                await w.CompleteAsync();
                info.IncrementCompletedCount();
            }
            else if (RandomData.GetBool())
            {
                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Abandoning: {Id}", w.Value.Id);
                await w.AbandonAsync();
                info.IncrementAbandonCount();
            }
            else
            {
                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Erroring: {Id}", w.Value.Id);
                info.IncrementErrorCount();
                throw new Exception();
            }
        }
        finally
        {
            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Signal {CurrentCount}", countdown.CurrentCount);
            countdown.Signal();
        }
    }

    protected async Task AssertEmptyQueueAsync(IQueue<SimpleWorkItem> queue)
    {
        if (_assertStats)
        {
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
    }

    public virtual async Task MaintainJobNotAbandon_NotWorkTimeOutEntry()
    {
        var queue = GetQueue(retries: 0, workItemTimeout: TimeSpan.FromMilliseconds(100), retryDelay: TimeSpan.Zero);
        if (queue == null)
            return;
        try
        {
            await queue.DeleteQueueAsync();
            await AssertEmptyQueueAsync(queue);
            queue.EnqueueAsync(new SimpleWorkItem
            {
                Data = "Hello World",
                Id = 1
            });
            queue.EnqueueAsync(new SimpleWorkItem
            {
                Data = "Hello World",
                Id = 2
            });

            var dequeuedQueueItem = Assert.IsType<QueueEntry<SimpleWorkItem>>(await queue.DequeueAsync());
            Assert.NotNull(dequeuedQueueItem.Value);
            // The first dequeued item works for 60 milliseconds less than work timeout(100 milliseconds).
            await Task.Delay(60);
            await dequeuedQueueItem.CompleteAsync();
            Assert.True(dequeuedQueueItem.IsCompleted);
            Assert.False(dequeuedQueueItem.IsAbandoned);

            dequeuedQueueItem = Assert.IsType<QueueEntry<SimpleWorkItem>>(await queue.DequeueAsync());
            Assert.NotNull(dequeuedQueueItem.Value);
            // The second dequeued item works for 60 milliseconds less than work timeout(100 milliseconds).
            await Task.Delay(60);
            await dequeuedQueueItem.CompleteAsync();
            Assert.True(dequeuedQueueItem.IsCompleted);
            Assert.False(dequeuedQueueItem.IsAbandoned);

            if (_assertStats)
            {
                var stats = await queue.GetQueueStatsAsync();
                Assert.Equal(0, stats.Working);
                Assert.Equal(0, stats.Abandoned);
                Assert.Equal(2, stats.Completed);
            }
        }
        finally
        {
            await CleanupQueueAsync(queue);
        }
    }

    public virtual async Task CanHandleAutoAbandonInWorker()
    {
        // create queue with short work item timeout so it will be auto abandoned
        var queue = GetQueue(workItemTimeout: TimeSpan.FromMilliseconds(100));
        if (queue == null)
            return;

        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await queue.DeleteQueueAsync();

            var successEvent = new AsyncAutoResetEvent();
            var errorEvent = new AsyncAutoResetEvent();

            await queue.StartWorkingAsync(async (item) =>
            {
                _logger.LogDebug("Processing item: {Id} Value={Value}", item.Id, item.Value.Data);
                if (item.Value.Data == "Delay")
                {
                    // wait for queue item to get auto abandoned
                    var stats = await queue.GetQueueStatsAsync();
                    var sw = Stopwatch.StartNew();
                    do
                    {
                        if (stats.Abandoned > 0)
                        {
                            _logger.LogTrace("Breaking, queue item was abandoned");
                            break;
                        }

                        stats = await queue.GetQueueStatsAsync();
                        _logger.LogTrace("Getting updated stats, Abandoned={Abandoned}", stats.Abandoned);

                        await Task.Delay(50, cancellationTokenSource.Token);
                    } while (sw.Elapsed < TimeSpan.FromSeconds(5));

                    Assert.Equal(1, stats.Abandoned);
                }

                try
                {
                    await item.CompleteAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error completing item: {Message}", ex.Message);
                    errorEvent.Set();
                    throw;
                }

                successEvent.Set();
            }, cancellationToken: cancellationTokenSource.Token);

            await queue.EnqueueAsync(new SimpleWorkItem() { Data = "Delay" });
            await queue.EnqueueAsync(new SimpleWorkItem() { Data = "No Delay" });

            await errorEvent.WaitAsync(TimeSpan.FromSeconds(10));
            await successEvent.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await cancellationTokenSource.CancelAsync();
            await CleanupQueueAsync(queue);
        }
    }

    public virtual void Dispose()
    {
        var queue = GetQueue();
        if (queue == null)
            return;

        using (queue)
            _ = Task.Run(queue.DeleteQueueAsync);

        GC.SuppressFinalize(this);
    }
}

public class WorkInfo
{
    private int _abandonCount;
    private int _errorCount;
    private int _completedCount;

    public int AbandonCount => _abandonCount;
    public int ErrorCount => _errorCount;
    public int CompletedCount => _completedCount;

    public void IncrementAbandonCount()
    {
        Interlocked.Increment(ref _abandonCount);
    }

    public void IncrementErrorCount()
    {
        Interlocked.Increment(ref _errorCount);
    }

    public void IncrementCompletedCount()
    {
        Interlocked.Increment(ref _completedCount);
    }
}
