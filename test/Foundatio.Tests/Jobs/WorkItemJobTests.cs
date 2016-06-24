using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Logging;
using Foundatio.Logging.Xunit;
using Foundatio.Messaging;
using Foundatio.Metrics;
using Foundatio.Queues;
using Foundatio.ServiceProviders;
using Foundatio.Tests.Utility;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Jobs {
    public class WorkItemJobTests : TestWithLoggingBase {
        public WorkItemJobTests(ITestOutputHelper output) : base(output) {}

        [Fact]
        public async Task CanRunWorkItem() {
            var queue = new InMemoryQueue<WorkItemData>();
            var messageBus = new InMemoryMessageBus();
            var handlerRegistry = new WorkItemHandlers();
            var job = new WorkItemJob(queue, messageBus, handlerRegistry, Log);

            handlerRegistry.Register<MyWorkItem>(async ctx => {
                var jobData = ctx.GetData<MyWorkItem>();
                Assert.Equal("Test", jobData.SomeData);

                for (int i = 0; i < 10; i++) {
                    await Task.Delay(100);
                    await ctx.ReportProgressAsync(10 * i);
                }
            });

            var jobId = await queue.EnqueueAsync(new MyWorkItem {
                SomeData = "Test"
            }, true);

            int statusCount = 0;
            messageBus.Subscribe<WorkItemStatus>(status => {
                _logger.Trace("Progress: {progress}", status.Progress);
                Assert.Equal(jobId, status.WorkItemId);
                statusCount++;
            });

            await job.RunUntilEmptyAsync();

            Assert.Equal(12, statusCount);
        }

        [Fact]
        public async Task CanHandleMultipleWorkItemInstances() {
            const int workItemCount = 1000;

            var metrics = new InMemoryMetricsClient(loggerFactory: Log);
            var queue = new InMemoryQueue<WorkItemData>(retries: 0, retryDelay: TimeSpan.Zero, loggerFactory: Log);
            queue.AttachBehavior(new MetricsQueueBehavior<WorkItemData>(metrics, loggerFactory: Log));
            var messageBus = new InMemoryMessageBus(Log);
            var handlerRegistry = new WorkItemHandlers();
            var j1 = new WorkItemJob(queue, messageBus, handlerRegistry, Log);
            var j2 = new WorkItemJob(queue, messageBus, handlerRegistry, Log);
            var j3 = new WorkItemJob(queue, messageBus, handlerRegistry, Log);
            int errors = 0;

            var jobIds = new ConcurrentDictionary<string, int>();

            handlerRegistry.Register<MyWorkItem>(async ctx => {
                var jobData = ctx.GetData<MyWorkItem>();
                Assert.Equal("Test", jobData.SomeData);

                var jobWorkTotal = jobIds.AddOrUpdate(ctx.JobId, 1, (key, value) => value + 1);
                if (jobData.Index % 100 == 0)
                    _logger.Trace("Job {jobId} processing work item #: {jobWorkTotal}", ctx.JobId, jobWorkTotal);

                for (int i = 0; i < 10; i++)
                    await ctx.ReportProgressAsync(10 * i);

                if (RandomData.GetBool(1)) {
                    Interlocked.Increment(ref errors);
                    throw new ApplicationException("Boom!");
                }
            });

            for (int i = 0; i < workItemCount; i++)
                await queue.EnqueueAsync(new MyWorkItem {
                    SomeData = "Test",
                    Index = i
                }, true);

            var completedItems = new List<string>();
            object completedItemsLock = new object();
            messageBus.Subscribe<WorkItemStatus>(status => {
                if (status.Progress == 100)
                    _logger.Trace("Progress: {progress}", status.Progress);

                if (status.Progress < 100)
                    return;

                lock (completedItemsLock)
                    completedItems.Add(status.WorkItemId);
            });
            
            var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var tasks = new List<Task> {
                Task.Run(async () => {
                    await j1.RunUntilEmptyAsync(cancellationTokenSource.Token);
                    cancellationTokenSource.Cancel();
                }, cancellationTokenSource.Token),
                Task.Run(async () => {
                    await j2.RunUntilEmptyAsync(cancellationTokenSource.Token);
                    cancellationTokenSource.Cancel();
                }, cancellationTokenSource.Token),
                Task.Run(async () => {
                    await j3.RunUntilEmptyAsync(cancellationTokenSource.Token);
                    cancellationTokenSource.Cancel();
                }, cancellationTokenSource.Token)
            };

            try {
                await Task.WhenAll(tasks);
                await Task.Delay(100);
            } catch (TaskCanceledException) {}

            _logger.Info("Completed: {completedItems} Errors: {errors}", completedItems.Count, errors);
            
            Assert.Equal(workItemCount, completedItems.Count + errors);
            Assert.Equal(3, jobIds.Count);
            Assert.Equal(workItemCount, jobIds.Sum(kvp => kvp.Value));
        }

        [Fact]
        public async Task CanRunWorkItemWithClassHandler() {
            var queue = new InMemoryQueue<WorkItemData>();
            var messageBus = new InMemoryMessageBus();
            var handlerRegistry = new WorkItemHandlers();
            var job = new WorkItemJob(queue, messageBus, handlerRegistry, Log);

            handlerRegistry.Register<MyWorkItem>(new MyWorkItemHandler(Log));

            var jobId = await queue.EnqueueAsync(new MyWorkItem {
                SomeData = "Test"
            }, true);

            int statusCount = 0;
            messageBus.Subscribe<WorkItemStatus>(status => {
                _logger.Trace("Progress: {progress}", status.Progress);
                Assert.Equal(jobId, status.WorkItemId);
                statusCount++;
            });

            await job.RunUntilEmptyAsync();

            Assert.Equal(11, statusCount);
        }

        [Fact]
        public async Task CanRunWorkItemWithDelegateHandler() {
            var queue = new InMemoryQueue<WorkItemData>();
            var messageBus = new InMemoryMessageBus();
            var handlerRegistry = new WorkItemHandlers();
            var job = new WorkItemJob(queue, messageBus, handlerRegistry, Log);

            handlerRegistry.Register<MyWorkItem>(async ctx => {
                var jobData = ctx.GetData<MyWorkItem>();
                Assert.Equal("Test", jobData.SomeData);

                for (int i = 1; i < 10; i++) {
                    await Task.Delay(100);
                    await ctx.ReportProgressAsync(10 * i);
                }
            }, Log.CreateLogger("MyWorkItem"));

            var jobId = await queue.EnqueueAsync(new MyWorkItem {
                SomeData = "Test"
            }, true);

            int statusCount = 0;
            messageBus.Subscribe<WorkItemStatus>(status => {
                _logger.Trace("Progress: {progress}", status.Progress);
                Assert.Equal(jobId, status.WorkItemId);
                statusCount++;
            });

            await job.RunUntilEmptyAsync();

            Assert.Equal(11, statusCount);
        }

        [Fact]
        public async Task CanRunBadWorkItem() {
            var queue = new InMemoryQueue<WorkItemData>(retries: 2, retryDelay: TimeSpan.FromMilliseconds(500));
            var messageBus = new InMemoryMessageBus();
            var handlerRegistry = new WorkItemHandlers();
            var job = new WorkItemJob(queue, messageBus, handlerRegistry, Log);

            handlerRegistry.Register<MyWorkItem>(ctx => {
                var jobData = ctx.GetData<MyWorkItem>();
                Assert.Equal("Test", jobData.SomeData);
                throw new ApplicationException();
            });

            var jobId = await queue.EnqueueAsync(new MyWorkItem {
                SomeData = "Test"
            }, true);

            int statusCount = 0;
            messageBus.Subscribe<WorkItemStatus>(status => {
                _logger.Trace("Progress: {progress}", status.Progress);
                Assert.Equal(jobId, status.WorkItemId);
                statusCount++;
            });

            await job.RunUntilEmptyAsync();
            Assert.Equal(1, statusCount);
        }
    }

    public class MyWorkItem {
        public string SomeData { get; set; }
        public int Index { get; set; }
    }

    public class MyWorkItemHandler : WorkItemHandlerBase {
        public MyWorkItemHandler(ILoggerFactory loggerFactory = null) : base(loggerFactory) { }

        public override async Task HandleItemAsync(WorkItemContext context) {
            var jobData = context.GetData<MyWorkItem>();
            Assert.Equal("Test", jobData.SomeData);

            for (int i = 1; i < 10; i++) {
                await Task.Delay(100);
                await context.ReportProgressAsync(10 * i);
            }
        }
    }
}