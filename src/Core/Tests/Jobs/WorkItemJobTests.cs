using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Jobs;
using Foundatio.Logging;
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
        public WorkItemJobTests(CaptureFixture fixture, ITestOutputHelper output) : base(fixture, output) {}

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
                    await ctx.ReportProgressAsync(10 * i).AnyContext();
                }
            });

            var jobId = await queue.EnqueueAsync(new MyWorkItem {
                SomeData = "Test"
            }, true).AnyContext();

            int statusCount = 0;
            messageBus.Subscribe<WorkItemStatus>(status => {
                Logger.Trace().Message($"Progress: {status.Progress}").Write();
                Assert.Equal(jobId, status.WorkItemId);
                statusCount++;
            });

            await job.RunUntilEmptyAsync().AnyContext();

            Assert.Equal(12, statusCount);
        }

        [Fact]
        public async Task CanHandleMultipleWorkItemInstances() {
            const int workItemCount = 10000;

            var metrics = new InMemoryMetricsClient();
            var queue = new InMemoryQueue<WorkItemData>(retries: 0, retryDelay: TimeSpan.Zero);
            queue.AttachBehavior(new MetricsQueueBehavior<WorkItemData>(metrics));
            var messageBus = new InMemoryMessageBus();
            var handlerRegistry = new WorkItemHandlers();
            var j1 = new WorkItemJob(queue, messageBus, handlerRegistry);
            var j2 = new WorkItemJob(queue, messageBus, handlerRegistry);
            var j3 = new WorkItemJob(queue, messageBus, handlerRegistry);
            int errors = 0;

            var jobIds = new ConcurrentDictionary<string, int>();

            handlerRegistry.Register<MyWorkItem>(async ctx => {
                var jobData = ctx.GetData<MyWorkItem>();
                Assert.Equal("Test", jobData.SomeData);

                var jobWorkTotal = jobIds.AddOrUpdate(ctx.JobId, 1, (key, value) => value + 1);
                Logger.Trace().Message($"Job {ctx.JobId} processing work item #: {jobWorkTotal}").Write();

                for (int i = 0; i < 10; i++)
                    await ctx.ReportProgressAsync(10 * i).AnyContext();

                if (RandomData.GetBool(1)) {
                    Interlocked.Increment(ref errors);
                    throw new ApplicationException("Boom!");
                }
            });

            for (int i = 0; i < workItemCount; i++)
                await queue.EnqueueAsync(new MyWorkItem {
                    SomeData = "Test",
                    Index = i
                }, true).AnyContext();

            var completedItems = new List<string>();
            object completedItemsLock = new object();
            messageBus.Subscribe<WorkItemStatus>(status => {
                Logger.Trace().Message($"Progress: {status.Progress}").Write();
                if (status.Progress < 100)
                    return;

                lock (completedItemsLock)
                    completedItems.Add(status.WorkItemId);
            });
            
            var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var tasks = new List<Task> {
                Task.Run(async () => {
                    await j1.RunUntilEmptyAsync(cancellationTokenSource.Token).AnyContext();
                    cancellationTokenSource.Cancel();
                }, cancellationTokenSource.Token),
                Task.Run(async () => {
                    await j2.RunUntilEmptyAsync(cancellationTokenSource.Token).AnyContext();
                    cancellationTokenSource.Cancel();
                }, cancellationTokenSource.Token),
                Task.Run(async () => {
                    await j3.RunUntilEmptyAsync(cancellationTokenSource.Token).AnyContext();
                    cancellationTokenSource.Cancel();
                }, cancellationTokenSource.Token)
            };

            try {
                await Task.WhenAll(tasks).AnyContext();
                await Task.Delay(100).AnyContext();
            } catch (TaskCanceledException) {}

            Logger.Info().Message($"Completed: {completedItems.Count} Errors: {errors}").Write();
            metrics.DisplayStats(_writer);
            
            Assert.Equal(workItemCount, completedItems.Count + errors);
            Assert.Equal(3, jobIds.Count);
            Assert.Equal(workItemCount, jobIds.Sum(kvp => kvp.Value));
        }

        [Fact]
        public async Task CanRunWorkItemWithClassHandler() {
            ServiceProvider.SetServiceProvider(typeof(MyBootstrappedServiceProvider));
            var queue = new InMemoryQueue<WorkItemData>();
            var messageBus = new InMemoryMessageBus();
            var handlerRegistry = new WorkItemHandlers();
            var job = new WorkItemJob(queue, messageBus, handlerRegistry);

            handlerRegistry.Register<MyWorkItem, MyWorkItemHandler>();

            var jobId = await queue.EnqueueAsync(new MyWorkItem {
                SomeData = "Test"
            }, true).AnyContext();

            int statusCount = 0;
            messageBus.Subscribe<WorkItemStatus>(status => {
                Logger.Trace().Message($"Progress: {status.Progress}").Write();
                Assert.Equal(jobId, status.WorkItemId);
                statusCount++;
            });

            await job.RunUntilEmptyAsync().AnyContext();

            Assert.Equal(11, statusCount);
        }

        [Fact]
        public async Task CanRunBadWorkItem() {
            var queue = new InMemoryQueue<WorkItemData>(retries: 2, retryDelay: TimeSpan.FromMilliseconds(500));
            var messageBus = new InMemoryMessageBus();
            var handlerRegistry = new WorkItemHandlers();
            var job = new WorkItemJob(queue, messageBus, handlerRegistry);

            handlerRegistry.Register<MyWorkItem>(ctx => {
                var jobData = ctx.GetData<MyWorkItem>();
                Assert.Equal("Test", jobData.SomeData);
                throw new ApplicationException();
            });

            var jobId = await queue.EnqueueAsync(new MyWorkItem {
                SomeData = "Test"
            }, true).AnyContext();

            int statusCount = 0;
            messageBus.Subscribe<WorkItemStatus>(status => {
                Logger.Trace().Message($"Progress: {status.Progress}").Write();
                Assert.Equal(jobId, status.WorkItemId);
                statusCount++;
            });

            await job.RunUntilEmptyAsync().AnyContext();
            Assert.Equal(3, statusCount);
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

        public override async Task HandleItemAsync(WorkItemContext context) {
            Assert.NotNull(Dependency);

            var jobData = context.GetData<MyWorkItem>();
            Assert.Equal("Test", jobData.SomeData);

            for (int i = 1; i < 10; i++) {
                await Task.Delay(100).AnyContext();
                await context.ReportProgressAsync(10 * i).AnyContext();
            }
        }
    }
}