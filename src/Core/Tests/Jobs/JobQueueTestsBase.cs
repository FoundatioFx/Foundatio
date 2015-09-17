using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Logging;
using Foundatio.Metrics;
using Foundatio.Queues;
using Foundatio.Tests.Utility;
using Foundatio.Utility;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Jobs {
    public abstract class JobQueueTestsBase: CaptureTests {
        public JobQueueTestsBase(CaptureFixture fixture, ITestOutputHelper output) : base(fixture, output) { }

        protected abstract IQueue<SampleQueueWorkItem> GetSampleWorkItemQueue(int retries, TimeSpan retryDelay);
        
        public virtual async Task CanRunQueueJob() {
            const int workItemCount = 100;
            var metrics = new InMemoryMetricsClient();
            var queue = GetSampleWorkItemQueue(0, TimeSpan.Zero);
            queue.AttachBehavior(new MetricsQueueBehavior<SampleQueueWorkItem>(metrics, "test"));

            metrics.StartDisplayingStats(TimeSpan.FromSeconds(1), _writer);
            var enqueueTask = Run.InParallel(workItemCount, async index => {
                await queue.EnqueueAsync(new SampleQueueWorkItem {
                    Created = DateTime.Now,
                    Path = "somepath" + index
                }).AnyContext();
            });

            var job = new SampleQueueJob(queue, metrics);
            await Task.WhenAll(job.RunUntilEmptyAsync(), enqueueTask).AnyContext();

            metrics.DisplayStats(_writer);

            var stats = await queue.GetQueueStatsAsync().AnyContext();
            Assert.Equal(0, stats.Queued);
            Assert.Equal(workItemCount, stats.Enqueued);
            Assert.Equal(workItemCount, stats.Dequeued);
        }
        
        public virtual async Task CanRunMultipleQueueJobs() {
            const int jobCount = 5;
            const int workItemCount = 15;
            var metrics = new InMemoryMetricsClient();
            metrics.StartDisplayingStats(TimeSpan.FromSeconds(1), _writer);

            var queues = new List<IQueue<SampleQueueWorkItem>>();
            for (int i = 0; i < jobCount; i++) {
                var q = GetSampleWorkItemQueue(retries: 3, retryDelay: TimeSpan.FromSeconds(1));
                q.AttachBehavior(new MetricsQueueBehavior<SampleQueueWorkItem>(metrics, "test"));
                queues.Add(q);
            }

            var enqueueTask = Run.InParallel(workItemCount, async index => {
                var queue = queues[RandomData.GetInt(0, jobCount - 1)];
                await queue.EnqueueAsync(new SampleQueueWorkItem {
                    Created = DateTime.Now,
                    Path = RandomData.GetString()
                }).AnyContext();
            });

            var cancellationTokenSource = new CancellationTokenSource();
            await Run.InParallel(jobCount, async index => {
                var queue = queues[index - 1];
                var job = new SampleQueueJob(queue, metrics);
                await job.RunUntilEmptyAsync(cancellationTokenSource.Token).AnyContext();
                cancellationTokenSource.Cancel();
            }).AnyContext();

            await enqueueTask.AnyContext();

            var queueStats = new List<QueueStats>();
            for (int i = 0; i < queues.Count; i++) {
                var stats = await queues[i].GetQueueStatsAsync().AnyContext();
                Logger.Info().Message($"Queue#{i}: Working: {stats.Working} Completed: {stats.Completed} Abandoned: {stats.Abandoned} Error: {stats.Errors} Deadletter: {stats.Deadletter}").Write();
                queueStats.Add(stats);
            }

            metrics.DisplayStats(_writer);
            Assert.Equal(metrics.GetCount("completed"), queueStats.Sum(s => s.Completed));
            Assert.InRange(queueStats.Sum(s => s.Completed), 0, workItemCount);
            //Assert.Equal(workItemCount, queueStats.Sum(s => s.Completed) + queueStats.LastOrDefault()?.Deadletter);
        }
    }
}