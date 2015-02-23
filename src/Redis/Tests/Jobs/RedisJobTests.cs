using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Metrics;
using Foundatio.Redis.Queues;
using Foundatio.Tests.Jobs;
using Foundatio.Tests.Utility;
using Xunit;

namespace Foundatio.Redis.Tests.Jobs {
    public class RedisJobTests {
        [Fact]
        public void CanRunQueueJob() {
            const int workItemCount = 10000;
            var metrics = new InMemoryMetricsClient();
            var countdown = new CountDownLatch(workItemCount);
            var queue = new RedisQueue<SampleQueueWorkItem>(SharedConnection.GetMuxer(), null, 0, TimeSpan.Zero, metrics: metrics);

            for (int i = 0; i < workItemCount; i++)
                queue.Enqueue(new SampleQueueWorkItem { Created = DateTime.Now, Path = "somepath" + i });

            var job = new SampleQueueJob(queue, metrics, countdown);
            var tokenSource = new CancellationTokenSource();
            Task.Factory.StartNew(() => job.RunContinuousAsync(token: tokenSource.Token), tokenSource.Token);
            bool success = countdown.Wait(3 * 60 * 1000);
            metrics.DisplayStats();

            Assert.Equal(0, queue.GetQueueCount());
        }
    }
}
