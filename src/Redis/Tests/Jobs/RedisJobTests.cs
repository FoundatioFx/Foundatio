using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Exceptionless;
using Foundatio.Metrics;
using Foundatio.Queues;
using Foundatio.Tests.Jobs;
using Foundatio.Tests.Utility;
using Xunit;
using Xunit.Abstractions;
using Foundatio.Extensions;
using Foundatio.Utility;

namespace Foundatio.Redis.Tests.Jobs {
    public class RedisJobTests : CaptureTests {
        public RedisJobTests(CaptureFixture fixture, ITestOutputHelper output) : base(fixture, output) {}

        [Fact]
        public async Task CanRunQueueJob() {
            const int workItemCount = 10000;
            var metrics = new InMemoryMetricsClient();
            var queue = new RedisQueue<SampleQueueWorkItem>(SharedConnection.GetMuxer(), null, null, 0, TimeSpan.Zero);
            queue.AttachBehavior(new MetricsQueueBehavior<SampleQueueWorkItem>(metrics, "test"));

            metrics.StartDisplayingStats(TimeSpan.FromMilliseconds(100), _writer);
            var enqueueTask = Run.InParallel(workItemCount, async index => {
                await queue.EnqueueAsync(new SampleQueueWorkItem {
                    Created = DateTime.Now,
                    Path = "somepath" + index
                }).AnyContext();
            });

            var job = new SampleQueueJob(queue, metrics);
            await job.RunUntilEmptyAsync().AnyContext();
            await enqueueTask.AnyContext();

            metrics.DisplayStats(_writer);

            Assert.Equal(0, (await queue.GetQueueStatsAsync().AnyContext()).Queued);
        }
        
        [Fact]
        public async Task CanRunMultipleQueueJobs() {
            const int jobCount = 5;
            const int workItemCount = 1000;
            var metrics = new InMemoryMetricsClient();
            metrics.StartDisplayingStats(TimeSpan.FromMilliseconds(100), _writer);

            var queues = new List<RedisQueue<SampleQueueWorkItem>>();
            for (int i = 0; i < jobCount; i++) {
                var q = new RedisQueue<SampleQueueWorkItem>(SharedConnection.GetMuxer(), retries: 3, retryDelay: TimeSpan.FromSeconds(1));
                q.AttachBehavior(new MetricsQueueBehavior<SampleQueueWorkItem>(metrics, "test"));
                queues.Add(q);
            }

            var enqueueTask = Run.InParallel(workItemCount, async index => {
                var queue = queues[RandomData.GetInt(0, 4)];
                await queue.EnqueueAsync(new SampleQueueWorkItem {
                    Created = DateTime.Now,
                    Path = RandomData.GetString()
                }).AnyContext();
            });

            await Run.InParallel(jobCount, async index => {
                var queue = queues[index];
                var job = new SampleQueueJob(queue, metrics);
                await job.RunUntilEmptyAsync().AnyContext();
            }).AnyContext();

            await enqueueTask.AnyContext();
            metrics.DisplayStats(_writer);
        }
    }
}