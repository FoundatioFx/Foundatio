using System;
using System.Threading.Tasks;
using Foundatio.Metrics;
using Foundatio.Queues;
using Foundatio.Tests.Jobs;
using Foundatio.Tests.Utility;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Redis.Tests.Jobs {
    public class RedisJobTests : CaptureTests {
        public RedisJobTests(CaptureFixture fixture, ITestOutputHelper output) : base(fixture, output) {}

        [Fact]
        public async Task CanRunQueueJob() {
            const int workItemCount = 10000;
            var metrics = new InMemoryMetricsClient();
            var queue = new RedisQueue<SampleQueueWorkItem>(SharedConnection.GetMuxer(), null, null, 0, TimeSpan.Zero);
            queue.AttachBehavior(new MetricsQueueBehavior<SampleQueueWorkItem>(metrics));

            metrics.StartDisplayingStats(TimeSpan.FromMilliseconds(100));
            Task.Factory.StartNew(() => {
                Parallel.For(0, workItemCount, async i => {
                    await queue.EnqueueAsync(new SampleQueueWorkItem { Created = DateTime.Now, Path = "somepath" + i });
                });
            });

            var job = new SampleQueueJob(queue, metrics);
            await job.RunUntilEmptyAsync();
            metrics.DisplayStats();

            Assert.Equal(0, (await queue.GetQueueStatsAsync()).Queued);
        }
    }
}
