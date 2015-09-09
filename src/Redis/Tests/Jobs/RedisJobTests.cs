#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed

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
            Task.Run(() => {
                Parallel.For(0, workItemCount, i => {
                    queue.EnqueueAsync(new SampleQueueWorkItem {
                        Created = DateTime.Now,
                        Path = "somepath" + i
                    }).AnyContext().GetAwaiter().GetResult();
                });
            });

            var job = new SampleQueueJob(queue, metrics);
            await job.RunUntilEmptyAsync().AnyContext();
            metrics.DisplayStats(_writer);

            Assert.Equal(0, (await queue.GetQueueStatsAsync().AnyContext()).Queued);
        }

        [Fact(Skip = "df")]
        public void CanRunMultipleQueueJobs() {
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

            Task.Run(() => {
                Parallel.For(0, workItemCount, i => {
                    var queue = queues[RandomData.GetInt(0, 4)];
                    queue.EnqueueAsync(new SampleQueueWorkItem {
                        Created = DateTime.Now,
                        Path = RandomData.GetString()
                    }).AnyContext().GetAwaiter().GetResult();
                });
            });

            Parallel.For(0, jobCount, index => {
                var queue = queues[index];
                var job = new SampleQueueJob(queue, metrics);
                job.RunUntilEmptyAsync().AnyContext().GetAwaiter().GetResult();
            });

            metrics.DisplayStats(_writer);
        }
    }
}

#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed