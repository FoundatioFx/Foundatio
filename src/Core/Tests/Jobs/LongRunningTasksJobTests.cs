using System;
using System.Threading;
using Foundatio.Jobs;
using Foundatio.Messaging;
using Foundatio.Queues;
using Xunit;

namespace Foundatio.Tests.Jobs {
    public class LongRunningTasksJobTests {
        [Fact]
        public void CanRunLongRunningTasks() {
            var queue = new InMemoryQueue<LongRunningTaskData>();
            var messageBus = new InMemoryMessageBus();
            var handlerRegistry = new LongRunningTaskHandlerRegistry();
            var job = new LongRunningTasksJob(queue, messageBus, handlerRegistry);

            handlerRegistry.RegisterHandler<MyLongRunningTask>(async ctx => {
                var jobData = ctx.GetData<MyLongRunningTask>();
                Assert.Equal("Test", jobData.SomeData);

                for (int i = 0; i < 10; i++) {
                    Thread.Sleep(100);
                    ctx.ReportProgress(10 * i);
                }
            });

            var jobId = queue.Enqueue(new MyLongRunningTask { SomeData = "Test" });

            int statusCount = 0;
            messageBus.Subscribe<LongRunningTaskStatus>(status => {
                Assert.Equal(jobId, status.JobId);
                statusCount++;
            });

            job.RunUntilEmpty();

            Assert.Equal(11, statusCount);
        }
    }

    public class MyLongRunningTask {
        public string SomeData { get; set; }
    }
}
