using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Jobs;
using Foundatio.Messaging;
using Foundatio.Metrics;
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
        public async Task CanRunWorkItem() {
            var queue = new InMemoryQueue<WorkItemData>();
            var messageBus = new InMemoryMessageBus();
            var handlerRegistry = new WorkItemHandlers();
            var job = new WorkItemJob(queue, messageBus, handlerRegistry);

            handlerRegistry.Register<MyWorkItem>(async ctx => {
                var jobData = ctx.GetData<MyWorkItem>();
                Assert.Equal("Test", jobData.SomeData);

                for (int i = 0; i < 10; i++) {
                    await Task.Delay(100).AnyContext();
                    ctx.ReportProgress(10 * i);
                }
            });

            var jobId = await queue.EnqueueAsync(new MyWorkItem { SomeData = "Test" }, true).AnyContext();

            int statusCount = 0;
            await messageBus.SubscribeAsync<WorkItemStatus>(status => {
                Assert.Equal(jobId, status.WorkItemId);
                statusCount++;
            }).AnyContext();

            await job.RunUntilEmptyAsync().AnyContext();

            Assert.Equal(12, statusCount);
        }

        [Fact]
        public async Task CanHandleMultipleWorkItemInstances() {
            var metrics = new InMemoryMetricsClient();
            var queue = new InMemoryQueue<WorkItemData>(retryDelay: TimeSpan.Zero, retries: 0);
            queue.AttachBehavior(new MetricsQueueBehavior<WorkItemData>(metrics));
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

                if (RandomData.GetBool(1)) {
                    Interlocked.Increment(ref errors);
                    throw new ApplicationException("Boom!");
                }
                
                return TaskHelper.Completed();
            });

            for (int i = 0; i < 100; i++)
                await queue.EnqueueAsync(new MyWorkItem { SomeData = "Test", Index = i }, true).AnyContext();
            
            var completedItems = new List<string>();
            object completedItemsLock = new object();
            await messageBus.SubscribeAsync<WorkItemStatus>(status => {
                if (status.Progress < 100)
                    return;

                lock (completedItemsLock)
                    completedItems.Add(status.WorkItemId);
            }).AnyContext();
            
            var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var token = cancellationTokenSource.Token;
            var tasks = new List<Task>();
            tasks.AddRange(new[] {
                Task.Run(async () => await j1.RunUntilEmptyAsync(token).AnyContext(), token),
                Task.Run(async () => await j2.RunUntilEmptyAsync(token).AnyContext(), token),
                Task.Run(async () => await j3.RunUntilEmptyAsync(token).AnyContext(), token),
            });

            await Task.WhenAll(tasks).AnyContext();
            await Task.Delay(10).AnyContext();
            Assert.Equal(100, completedItems.Count + errors);
            Assert.Equal(3, jobIds.Count);
            Assert.Equal(100, jobIds.Sum(kvp => kvp.Value));
        }

        [Fact]
        public async Task CanRunWorkItemWithClassHandler() {
            ServiceProvider.SetServiceProvider(typeof(MyBootstrappedServiceProvider));
            var queue = new InMemoryQueue<WorkItemData>();
            var messageBus = new InMemoryMessageBus();
            var handlerRegistry = new WorkItemHandlers();
            var job = new WorkItemJob(queue, messageBus, handlerRegistry);

            handlerRegistry.Register<MyWorkItem, MyWorkItemHandler>();

            var jobId = await queue.EnqueueAsync(new MyWorkItem { SomeData = "Test" }, true).AnyContext();

            int statusCount = 0;
            await messageBus.SubscribeAsync<WorkItemStatus>(status => {
                Console.WriteLine($"Progress: {status.Progress}");
                Assert.Equal(jobId, status.WorkItemId);
                statusCount++;
            }).AnyContext();

            await job.RunUntilEmptyAsync().AnyContext();

            Assert.Equal(11, statusCount);
        }

        [Fact]
        public async Task CanRunBadWorkItem() {
            var queue = new InMemoryQueue<WorkItemData>();
            var messageBus = new InMemoryMessageBus();
            var handlerRegistry = new WorkItemHandlers();
            var job = new WorkItemJob(queue, messageBus, handlerRegistry);

            handlerRegistry.Register<MyWorkItem>(ctx => {
                var jobData = ctx.GetData<MyWorkItem>();
                Assert.Equal("Test", jobData.SomeData);
                throw new ApplicationException();
            });

            var jobId = await queue.EnqueueAsync(new MyWorkItem { SomeData = "Test" }, true).AnyContext();

            int statusCount = 0;
            await messageBus.SubscribeAsync<WorkItemStatus>(status => {
                Assert.Equal(jobId, status.WorkItemId);
                statusCount++;
            }).AnyContext();

            await job.RunUntilEmptyAsync().AnyContext();
            Thread.Sleep(1);

            Assert.Equal(1, statusCount);

            handlerRegistry.Register<MyWorkItem>(ctx => {
                var jobData = ctx.GetData<MyWorkItem>();
                Assert.Equal("Test", jobData.SomeData);
                return TaskHelper.Completed();
            });

            jobId = await queue.EnqueueAsync(new MyWorkItem { SomeData = "Test" }, true).AnyContext();

            await job.RunUntilEmptyAsync().AnyContext();

            Assert.Equal(2, statusCount);
        }
    }

    public class MyWorkItem {
        public string SomeData { get; set; }
        public int Index { get; set; }
    }

    public class MyWorkItemHandler : WorkItemHandlerBase {
        public MyWorkItemHandler(MyDependency dependency) {
            Dependency = dependency;
        }

        public MyDependency Dependency { get; private set; }

        public override async Task HandleItem(WorkItemContext context) {
            Assert.NotNull(Dependency);

            var jobData = context.GetData<MyWorkItem>();
            Assert.Equal("Test", jobData.SomeData);

            for (int i = 1; i < 10; i++) {
                await Task.Delay(100);
                context.ReportProgress(10 * i);
            }
        }
    }
}
