using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Metrics;
using Foundatio.Queues;
using Xunit;

namespace Foundatio.Tests.Queue
{
    public class MetricQueueBehaviorTests
    {
        [Fact]
        public void SimpleWorkItemTest()
        {
            var eventRaised = new ManualResetEvent(false);

            var metricsClient = new InMemoryMetricsClient();
            var behavior = new MetricsQueueBehavior<SimpleWorkItem>(metricsClient, "metric");
            var queue = new InMemoryQueue<SimpleWorkItem>(behaviours: new[] { behavior });
            queue.Completed += (sender, e) => { eventRaised.Set(); };

            var work = new SimpleWorkItem { Id = 1, Data = "Testing" };

            queue.Enqueue(work);
            var item = queue.Dequeue();
            item.Complete();
            

            Assert.True(eventRaised.WaitOne(TimeSpan.FromMinutes(1)));

            Assert.Equal(3, metricsClient.Counters.Count);
            Assert.Equal(2, metricsClient.Timings.Count);

            Assert.Equal(1, metricsClient.Counters["metric.simpleworkitem.testing.enqueued"]?.CurrentValue);
            Assert.Equal(1, metricsClient.Counters["metric.simpleworkitem.testing.dequeued"]?.CurrentValue);
            Assert.Equal(1, metricsClient.Counters["metric.simpleworkitem.testing.completed"]?.CurrentValue);

            Assert.True(0 < metricsClient.Timings["metric.simpleworkitem.testing.queuetime"]?.Count);
            Assert.True(0 < metricsClient.Timings["metric.simpleworkitem.testing.processtime"]?.Count);
        }

        [Fact]
        public void WorkItemTest()
        {
            var eventRaised = new ManualResetEvent(false);

            var metricsClient = new InMemoryMetricsClient();
            var behavior = new MetricsQueueBehavior<WorkItemData>(metricsClient, "metric");
            var queue = new InMemoryQueue<WorkItemData>(behaviours: new[] { behavior });
            queue.Completed += (sender, e) => { eventRaised.Set(); };

            var work = new SimpleWorkItem { Id = 1, Data = "Testing" };

            queue.Enqueue(work);
            var item = queue.Dequeue();
            item.Complete();
            

            Assert.True(eventRaised.WaitOne(TimeSpan.FromMinutes(1)));

            Assert.Equal(3, metricsClient.Counters.Count);
            Assert.Equal(2, metricsClient.Timings.Count);

            Assert.Equal(1, metricsClient.Counters["metric.workitemdata.simpleworkitem.enqueued"]?.CurrentValue);
            Assert.Equal(1, metricsClient.Counters["metric.workitemdata.simpleworkitem.dequeued"]?.CurrentValue);
            Assert.Equal(1, metricsClient.Counters["metric.workitemdata.simpleworkitem.completed"]?.CurrentValue);

            Assert.True(0 < metricsClient.Timings["metric.workitemdata.simpleworkitem.queuetime"]?.Average);
            Assert.True(0 < metricsClient.Timings["metric.workitemdata.simpleworkitem.processtime"]?.Average);
        }
    }
}
