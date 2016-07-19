using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Exceptionless;
using Foundatio.Caching;
using Foundatio.Jobs;
using Foundatio.Lock;
using Foundatio.Logging;
using Foundatio.Logging.Xunit;
using Foundatio.Metrics;
using Foundatio.Queues;
using Foundatio.Utility;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Jobs {
    public abstract class JobQueueTestsBase: TestWithLoggingBase {
        public JobQueueTestsBase(ITestOutputHelper output) : base(output) {
            SystemClock.Reset();
        }

        protected abstract IQueue<SampleQueueWorkItem> GetSampleWorkItemQueue(int retries, TimeSpan retryDelay);
        
        public virtual async Task CanRunQueueJob() {
            const int workItemCount = 100;
            using (var queue = GetSampleWorkItemQueue(retries: 0, retryDelay: TimeSpan.Zero)) {
                await queue.DeleteQueueAsync();

                var enqueueTask = Run.InParallel(workItemCount, async index => {
                    await queue.EnqueueAsync(new SampleQueueWorkItem {
                        Created = SystemClock.UtcNow,
                        Path = "somepath" + index
                    });
                });

                var job = new SampleQueueJob(queue, null, Log);
                await SystemClock.SleepAsync(10);
                await Task.WhenAll(job.RunUntilEmptyAsync(), enqueueTask);

                var stats = await queue.GetQueueStatsAsync();
                Assert.Equal(0, stats.Queued);
                Assert.Equal(workItemCount, stats.Enqueued);
                Assert.Equal(workItemCount, stats.Dequeued);
            }
        }

        public virtual async Task CanRunQueueJobWithLockFail() {
            const int workItemCount = 10;
            const int allowedLockCount = 5;
            using (var queue = GetSampleWorkItemQueue(retries: 3, retryDelay: TimeSpan.Zero)) {
                await queue.DeleteQueueAsync();
            
                var enqueueTask = Run.InParallel(workItemCount, async index => {
                    await queue.EnqueueAsync(new SampleQueueWorkItem {
                        Created = SystemClock.UtcNow,
                        Path = "somepath" + index
                    });
                });

                var lockProvider = new ThrottlingLockProvider(new InMemoryCacheClient(), allowedLockCount, TimeSpan.FromDays(1), Log);
                var job = new SampleQueueJobWithLocking(queue, null, lockProvider, Log);
                await SystemClock.SleepAsync(10);
                await Task.WhenAll(job.RunUntilEmptyAsync(), enqueueTask);

                var stats = await queue.GetQueueStatsAsync();
                Assert.Equal(0, stats.Queued);
                Assert.Equal(workItemCount, stats.Enqueued);
                Assert.Equal(allowedLockCount, stats.Completed);
                Assert.Equal(allowedLockCount * 4, stats.Abandoned);
                Assert.Equal(allowedLockCount, stats.Deadletter);
            }
        }

        public virtual async Task CanRunMultipleQueueJobs() {
            const int jobCount = 5;
            const int workItemCount = 100;

            Log.SetLogLevel<SampleQueueJob>(LogLevel.Information);
            Log.SetLogLevel<InMemoryMetricsClient>(LogLevel.None);

            using (var metrics = new InMemoryMetricsClient(true, loggerFactory: Log)) {
                var queues = new List<IQueue<SampleQueueWorkItem>>();
                try {
                    for (int i = 0; i < jobCount; i++) {
                        var q = GetSampleWorkItemQueue(retries: 1, retryDelay: TimeSpan.Zero);
                        await q.DeleteQueueAsync();
                        q.AttachBehavior(new MetricsQueueBehavior<SampleQueueWorkItem>(metrics, "test", Log));
                        queues.Add(q);
                    }
                    _logger.Info("Done setting up queues");

                    var enqueueTask = Run.InParallel(workItemCount, async index => {
                        var queue = queues[RandomData.GetInt(0, jobCount - 1)];
                        await queue.EnqueueAsync(new SampleQueueWorkItem {
                            Created = SystemClock.UtcNow,
                            Path = RandomData.GetString()
                        });
                    });
                    _logger.Info("Done enqueueing");

                    var cancellationTokenSource = new CancellationTokenSource();
                    await Run.InParallel(jobCount, async index => {
                        var queue = queues[index - 1];
                        var job = new SampleQueueJob(queue, metrics, Log);
                        await job.RunUntilEmptyAsync(cancellationTokenSource.Token);
                        cancellationTokenSource.Cancel();
                    });
                    _logger.Info("Done running jobs until empty");

                    await enqueueTask;

                    var queueStats = new List<QueueStats>();
                    for (int i = 0; i < queues.Count; i++) {
                        var stats = await queues[i].GetQueueStatsAsync();
                        _logger.Info("Queue#{i}: Working: {working} Completed: {completed} Abandoned: {abandoned} Error: {errors} Deadletter: {deadletter}", i, stats.Working, stats.Completed, stats.Abandoned, stats.Errors, stats.Deadletter);
                        queueStats.Add(stats);
                    }
                    _logger.Info("Done getting queue stats");

                    await metrics.FlushAsync();
                    _logger.Info("Done flushing metrics");

                    var queueSummary = await metrics.GetQueueStatsAsync("test.samplequeueworkitem");
                    Assert.Equal(queueStats.Sum(s => s.Completed), queueSummary.Completed.Count);
                    Assert.InRange(queueStats.Sum(s => s.Completed), 0, workItemCount);
                } finally {
                    foreach (var q in queues) {
                        await q.DeleteQueueAsync();
                        q.Dispose();
                    }
                }
            }
        }
    }
}