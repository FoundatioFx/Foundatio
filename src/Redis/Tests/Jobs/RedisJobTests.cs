using System;
using System.Threading.Tasks;
using Foundatio.Metrics;
using Foundatio.Redis.Queues;
using Foundatio.Tests.Jobs;
using Xunit;

namespace Foundatio.Redis.Tests.Jobs {
    public class RedisJobTests {
        [Fact]
        public void CanRunQueueJob() {
            const int workItemCount = 10000;
            var metrics = new InMemoryMetricsClient();
           var queue = new RedisQueue<SampleQueueWorkItem>(SharedConnection.GetMuxer(), null, 0, TimeSpan.Zero, metrics: metrics);

            Task.Factory.StartNew(() => {
                Parallel.For(0, workItemCount, i => {
                    queue.Enqueue(new SampleQueueWorkItem { Created = DateTime.Now, Path = "somepath" + i });
                });
            });

            var job = new SampleQueueJob(queue, metrics);
            job.RunUntilEmpty();
            metrics.DisplayStats();

            Assert.Equal(0, queue.GetQueueCount());
        }
    }
}
