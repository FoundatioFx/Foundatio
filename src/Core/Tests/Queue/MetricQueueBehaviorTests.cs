using System;
using System.Threading;
using Foundatio.Jobs;
using Foundatio.Metrics;
using Foundatio.Queues;
using Foundatio.Tests.Utility;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Queue
{
    public class MetricQueueBehaviorTests : CaptureTests
    {
        public MetricQueueBehaviorTests(CaptureFixture fixture, ITestOutputHelper output) : base(fixture, output)
        {
        }

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

            metricsClient.DisplayStats(_writer);

            Assert.True(eventRaised.WaitOne(TimeSpan.FromMinutes(1)));

            Assert.Equal(6, metricsClient.Counters.Count);
            Assert.Equal(4, metricsClient.Timings.Count);

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

            Assert.Equal(6, metricsClient.Counters.Count);
            Assert.Equal(4, metricsClient.Timings.Count);

            Assert.Equal(1, metricsClient.Counters["metric.workitemdata.simpleworkitem.enqueued"]?.CurrentValue);
            Assert.Equal(1, metricsClient.Counters["metric.workitemdata.simpleworkitem.dequeued"]?.CurrentValue);
            Assert.Equal(1, metricsClient.Counters["metric.workitemdata.simpleworkitem.completed"]?.CurrentValue);

            Assert.True(0 < metricsClient.Timings["metric.workitemdata.simpleworkitem.queuetime"]?.Count);
            Assert.True(0 < metricsClient.Timings["metric.workitemdata.simpleworkitem.processtime"]?.Count);
        }
    }
}
