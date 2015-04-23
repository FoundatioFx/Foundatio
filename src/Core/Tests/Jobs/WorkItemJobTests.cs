using System;
using System.Configuration;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Messaging;
using Foundatio.Queues;
using Foundatio.ServiceProviders;
using Foundatio.Utility;
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
                Assert.Equal(jobId, status.WorkItemId);
                statusCount++;
            });

            job.RunUntilEmpty();

            Assert.Equal(12, statusCount);
        }

        [Fact]
        public void CanRunWorkItemWithClassHandler() {
            ServiceProvider.SetServiceProvider(typeof(MyBootstrappedServiceProvider));
            var queue = new InMemoryQueue<WorkItemData>();
            var messageBus = new InMemoryMessageBus();
            var handlerRegistry = new WorkItemHandlers();
            var job = new WorkItemJob(queue, messageBus, handlerRegistry);

            handlerRegistry.Register<MyWorkItem, MyWorkItemHandler>();

            var jobId = queue.Enqueue(new MyWorkItem { SomeData = "Test" });

            int statusCount = 0;
            messageBus.Subscribe<WorkItemStatus>(status => {
                Assert.Equal(jobId, status.WorkItemId);
                statusCount++;
            });

            job.RunUntilEmpty();

            Assert.Equal(12, statusCount);
        }

        [Fact]
        public void CanRunBadWorkItem() {
            var queue = new InMemoryQueue<WorkItemData>();
            var messageBus = new InMemoryMessageBus();
            var handlerRegistry = new WorkItemHandlers();
            var job = new WorkItemJob(queue, messageBus, handlerRegistry);

            handlerRegistry.Register<MyWorkItem>(async ctx => {
                var jobData = ctx.GetData<MyWorkItem>();
                Assert.Equal("Test", jobData.SomeData);
                throw new ApplicationException();
            });

            var jobId = queue.Enqueue(new MyWorkItem { SomeData = "Test" });

            int statusCount = 0;
            messageBus.Subscribe<WorkItemStatus>(status => {
                Assert.Equal(jobId, status.WorkItemId);
                statusCount++;
            });

            job.RunUntilEmpty();

            Assert.Equal(1, statusCount);

            handlerRegistry.Register<MyWorkItem>(async ctx => {
                var jobData = ctx.GetData<MyWorkItem>();
                Assert.Equal("Test", jobData.SomeData);
            });

            jobId = queue.Enqueue(new MyWorkItem { SomeData = "Test" });

            job.RunUntilEmpty();

            Assert.Equal(2, statusCount);
        }
    }

    public class MyWorkItem {
        public string SomeData { get; set; }
    }

    public class MyWorkItemHandler : IWorkItemHandler {
        public MyWorkItemHandler(MyDependency dependency) {
            Dependency = dependency;
        }

        public MyDependency Dependency { get; private set; }

        public Task HandleItem(WorkItemContext context) {
            Assert.NotNull(Dependency);

            var jobData = context.GetData<MyWorkItem>();
            Assert.Equal("Test", jobData.SomeData);

            for (int i = 0; i < 10; i++) {
                Thread.Sleep(100);
                context.ReportProgress(10 * i);
            }

            return TaskHelper.Completed();
        }
    }
}
