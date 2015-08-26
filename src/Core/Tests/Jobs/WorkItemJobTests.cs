using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Messaging;
using Foundatio.Queues;
using Foundatio.ServiceProviders;
using Foundatio.Tests.Utility;
using Foundatio.Utility;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Jobs {
    public class WorkItemJobTests : CaptureTests {
        public WorkItemJobTests(CaptureFixture fixture, ITestOutputHelper output) : base(fixture, output)
        {
        }

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
                    await Task.Delay(100);
                    ctx.ReportProgress(10 * i);
                }
            });

            var jobId = queue.Enqueue(new MyWorkItem { SomeData = "Test" }, true);

            int statusCount = 0;
            messageBus.Subscribe<WorkItemStatus>(status => {
                Assert.Equal(jobId, status.WorkItemId);
                statusCount++;
            });

            job.RunUntilEmpty();

            Assert.Equal(12, statusCount);
        }

        [Fact]
        public async Task CanHandleMultipleWorkItemInstances()
        {
            var queue = new InMemoryQueue<WorkItemData>(retryDelay: TimeSpan.Zero, retries: 0);
            var messageBus = new InMemoryMessageBus();
            var handlerRegistry = new WorkItemHandlers();
            var j1 = new WorkItemJob(queue, messageBus, handlerRegistry);
            var j2 = new WorkItemJob(queue, messageBus, handlerRegistry);
            var j3 = new WorkItemJob(queue, messageBus, handlerRegistry);
            int errors = 0;
            var jobIds = new ConcurrentDictionary<string, int>();

            handlerRegistry.Register<MyWorkItem>(ctx => {
                var jobData = ctx.GetData<MyWorkItem>();
                Assert.Equal("Test", jobData.SomeData);

                jobIds.AddOrUpdate(ctx.JobId, 1, (key, value) => value + 1);
                
                for (int i = 0; i < 10; i++)
                    ctx.ReportProgress(10 * i);

                if (RandomData.GetBool(1))
                {
                    Interlocked.Increment(ref errors);
                    throw new ApplicationException("Boom!");
                }

                return TaskHelper.Completed();
            });

            for (int i = 0; i < 100; i++)
                queue.Enqueue(new MyWorkItem { SomeData = "Test", Index = i }, true);
            
            var completedItems = new List<string>();
            object completedItemsLock = new object();
            messageBus.Subscribe<WorkItemStatus>(status =>
            {
                if (status.Progress < 100)
                    return;

                lock (completedItemsLock)
                    completedItems.Add(status.WorkItemId);
            });
            
            var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var token = cancellationTokenSource.Token;
            var tasks = new List<Task>();
            tasks.AddRange(new[] {
                Task.Run(async () => await j1.RunUntilEmptyAsync(token), token),
                Task.Run(async () => await j2.RunUntilEmptyAsync(token), token),
                Task.Run(async () => await j3.RunUntilEmptyAsync(token), token),
            });

            await Task.WhenAny(tasks);
            cancellationTokenSource.Cancel();
            await Task.WhenAll(tasks);
            Thread.Sleep(1);
            Assert.Equal(100, completedItems.Count + errors);
            Assert.Equal(3, jobIds.Count);
            Assert.Equal(100, jobIds.Sum(kvp => kvp.Value));
        }

        [Fact]
        public void CanRunWorkItemWithClassHandler() {
            ServiceProvider.SetServiceProvider(typeof(MyBootstrappedServiceProvider));
            var queue = new InMemoryQueue<WorkItemData>();
            var messageBus = new InMemoryMessageBus();
            var handlerRegistry = new WorkItemHandlers();
            var job = new WorkItemJob(queue, messageBus, handlerRegistry);

            handlerRegistry.Register<MyWorkItem, MyWorkItemHandler>();

            var jobId = queue.Enqueue(new MyWorkItem { SomeData = "Test" }, true);

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

            handlerRegistry.Register<MyWorkItem>(ctx => {
                var jobData = ctx.GetData<MyWorkItem>();
                Assert.Equal("Test", jobData.SomeData);
                throw new ApplicationException();
            });

            var jobId = queue.Enqueue(new MyWorkItem { SomeData = "Test" }, true);

            int statusCount = 0;
            messageBus.Subscribe<WorkItemStatus>(status => {
                Assert.Equal(jobId, status.WorkItemId);
                statusCount++;
            });

            job.RunUntilEmpty();
            Thread.Sleep(1);

            Assert.Equal(1, statusCount);

            handlerRegistry.Register<MyWorkItem>(ctx => {
                var jobData = ctx.GetData<MyWorkItem>();
                Assert.Equal("Test", jobData.SomeData);
                return TaskHelper.Completed();
            });

            jobId = queue.Enqueue(new MyWorkItem { SomeData = "Test" }, true);

            job.RunUntilEmpty();

            Assert.Equal(2, statusCount);
        }
    }

    public class MyWorkItem {
        public string SomeData { get; set; }
        public int Index { get; set; }
    }

    public class MyWorkItemHandler : WorkItemHandlerBase
    {
        public MyWorkItemHandler(MyDependency dependency) {
            Dependency = dependency;
        }

        public MyDependency Dependency { get; private set; }

        public override Task HandleItem(WorkItemContext context) {
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
