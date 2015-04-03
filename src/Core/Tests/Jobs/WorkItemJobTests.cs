using System;
using System.Threading;
using Foundatio.Jobs;
using Foundatio.Messaging;
using Foundatio.Queues;
using Xunit;

namespace Foundatio.Tests.Jobs {
    public class WorkItemJobTests {
        [Fact]
        public void CanRunWorkItem() {
            var queue = new InMemoryQueue<WorkItemData>();
            var messageBus = new InMemoryMessageBus();
            var handlerRegistry = new WorkItemHandlers();
            var job = new WorkItemJob(queue, messageBus, handlerRegistry);

            handlerRegistry.Register<MyWorkItem>(async ctx => {
                var jobData = ctx.GetData<MyWorkItem>();
                Assert.Equal("Test", jobData.SomeData);

                for (int i = 0; i < 10; i++) {
                    Thread.Sleep(100);
                    ctx.ReportProgress(10 * i);
                }
            });

            var jobId = queue.Enqueue(new MyWorkItem { SomeData = "Test" });

            int statusCount = 0;
            messageBus.Subscribe<WorkItemStatus>(status => {
                Assert.Equal(jobId, status.TaskId);
                statusCount++;
            });

            job.RunUntilEmpty();

            Assert.Equal(11, statusCount);
        }
    }

    public class MyWorkItem {
        public string SomeData { get; set; }
    }
}
