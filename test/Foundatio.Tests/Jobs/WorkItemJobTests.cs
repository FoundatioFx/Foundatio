﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Exceptionless;
using Foundatio.AsyncEx;
using Foundatio.Jobs;
using Foundatio.Logging.Xunit;
using Foundatio.Messaging;
using Foundatio.Metrics;
using Foundatio.Queues;
using Foundatio.Tests.Extensions;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Jobs {
    public class WorkItemJobTests : TestWithLoggingBase {
        public WorkItemJobTests(ITestOutputHelper output) : base(output) {}

        [Fact]
        public async Task CanRunWorkItem() {
            using (var queue = new InMemoryQueue<WorkItemData>(o => o.LoggerFactory(Log))) {
                using (var messageBus = new InMemoryMessageBus(o => o.LoggerFactory(Log))) {
                    var handlerRegistry = new WorkItemHandlers();
                    var job = new WorkItemJob(queue, messageBus, handlerRegistry, Log);

                    handlerRegistry.Register<MyWorkItem>(async ctx => {
                        var jobData = ctx.GetData<MyWorkItem>();
                        Assert.Equal("Test", jobData.SomeData);

                        for (int i = 0; i < 10; i++) {
                            await SystemClock.SleepAsync(100);
                            await ctx.ReportProgressAsync(10 * i);
                        }
                    });

                    string jobId = await queue.EnqueueAsync(new MyWorkItem {
                        SomeData = "Test"
                    }, true);


                    var countdown = new AsyncCountdownEvent(12);
                    await messageBus.SubscribeAsync<WorkItemStatus>(status => {
                        _logger.LogInformation("Progress: {Progress}", status.Progress);
                        Assert.Equal(jobId, status.WorkItemId);
                        countdown.Signal();
                    });

                    await job.RunAsync();
                    await countdown.WaitAsync(TimeSpan.FromSeconds(2));
                    Assert.Equal(0, countdown.CurrentCount);
                }
            }
        }

        [Fact]
        public async Task CanHandleMultipleWorkItemInstances() {
            const int workItemCount = 1000;

            using (var metrics = new InMemoryMetricsClient(o => o.LoggerFactory(Log))) {
                using (var queue = new InMemoryQueue<WorkItemData>(o => o.RetryDelay(TimeSpan.Zero).Retries(0).LoggerFactory(Log))) {
                    queue.AttachBehavior(new MetricsQueueBehavior<WorkItemData>(metrics, loggerFactory: Log));
                    using (var messageBus = new InMemoryMessageBus(o => o.LoggerFactory(Log))) {
                        var handlerRegistry = new WorkItemHandlers();
                        var j1 = new WorkItemJob(queue, messageBus, handlerRegistry, Log);
                        var j2 = new WorkItemJob(queue, messageBus, handlerRegistry, Log);
                        var j3 = new WorkItemJob(queue, messageBus, handlerRegistry, Log);
                        int errors = 0;

                        var jobIds = new ConcurrentDictionary<string, int>();

                        handlerRegistry.Register<MyWorkItem>(async ctx => {
                            var jobData = ctx.GetData<MyWorkItem>();
                            Assert.Equal("Test", jobData.SomeData);

                            int jobWorkTotal = jobIds.AddOrUpdate(ctx.JobId, 1, (key, value) => value + 1);
                            if (jobData.Index % 100 == 0 && _logger.IsEnabled(LogLevel.Trace))
                                _logger.LogTrace("Job {JobId} processing work item #: {JobWorkTotal}", ctx.JobId, jobWorkTotal);

                            for (int i = 0; i < 10; i++)
                                await ctx.ReportProgressAsync(10 * i);

                            if (RandomData.GetBool(1)) {
                                Interlocked.Increment(ref errors);
                                throw new Exception("Boom!");
                            }
                        });

                        for (int i = 0; i < workItemCount; i++)
                            await queue.EnqueueAsync(new MyWorkItem {
                                SomeData = "Test",
                                Index = i
                            }, true);

                        var completedItems = new List<string>();
                        object completedItemsLock = new object();
                        await messageBus.SubscribeAsync<WorkItemStatus>(status => {
                            if (status.Progress == 100 && _logger.IsEnabled(LogLevel.Trace))
                                _logger.LogTrace("Progress: {Progress}", status.Progress);

                            if (status.Progress < 100)
                                return;

                            lock (completedItemsLock)
                                completedItems.Add(status.WorkItemId);
                        });

                        var cancellationTokenSource = new CancellationTokenSource(10000);
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
                        } catch (OperationCanceledException ex) {
                            if (_logger.IsEnabled(LogLevel.Error))
                                _logger.LogError(ex, "One or more tasks were cancelled: {Message}", ex.Message);
                        }

                        await SystemClock.SleepAsync(100);
                        if (_logger.IsEnabled(LogLevel.Information))
                            _logger.LogInformation("Completed: {CompletedItems} Errors: {Errors}", completedItems.Count, errors);
                        Assert.Equal(workItemCount, completedItems.Count + errors);
                        Assert.Equal(3, jobIds.Count);
                        Assert.Equal(workItemCount, jobIds.Sum(kvp => kvp.Value));
                    }
                }
            }
        }

        [Fact]
        public async Task CanRunWorkItemWithClassHandler() {
            using (var queue = new InMemoryQueue<WorkItemData>(o => o.LoggerFactory(Log))) {
                using (var messageBus = new InMemoryMessageBus(o => o.LoggerFactory(Log))) {
                    var handlerRegistry = new WorkItemHandlers();
                    var job = new WorkItemJob(queue, messageBus, handlerRegistry, Log);

                    handlerRegistry.Register<MyWorkItem>(new MyWorkItemHandler(Log));

                    string jobId = await queue.EnqueueAsync(new MyWorkItem {
                        SomeData = "Test"
                    }, true);

                    var countdown = new AsyncCountdownEvent(11);
                    await messageBus.SubscribeAsync<WorkItemStatus>(status => {
                        if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Progress: {Progress}", status.Progress);
                        Assert.Equal(jobId, status.WorkItemId);
                        countdown.Signal();
                    });

                    await job.RunUntilEmptyAsync();
                    await countdown.WaitAsync(TimeSpan.FromSeconds(2));
                    Assert.Equal(0, countdown.CurrentCount);
                }
            }
        }

        [Fact]
        public async Task CanRunWorkItemWithDelegateHandler() {
            using (var queue = new InMemoryQueue<WorkItemData>(o => o.LoggerFactory(Log))) {
                using (var messageBus = new InMemoryMessageBus(o => o.LoggerFactory(Log))) {
                    var handlerRegistry = new WorkItemHandlers();
                    var job = new WorkItemJob(queue, messageBus, handlerRegistry, Log);

                    handlerRegistry.Register<MyWorkItem>(async ctx => {
                        var jobData = ctx.GetData<MyWorkItem>();
                        Assert.Equal("Test", jobData.SomeData);

                        for (int i = 1; i < 10; i++) {
                            await SystemClock.SleepAsync(100);
                            await ctx.ReportProgressAsync(10 * i);
                        }
                    }, Log.CreateLogger("MyWorkItem"));

                    string jobId = await queue.EnqueueAsync(new MyWorkItem {
                        SomeData = "Test"
                    }, true);

                    var countdown = new AsyncCountdownEvent(11);
                    await messageBus.SubscribeAsync<WorkItemStatus>(status => {
                        if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Progress: {Progress}", status.Progress);
                        Assert.Equal(jobId, status.WorkItemId);
                        countdown.Signal();
                    });

                    await job.RunUntilEmptyAsync();
                    await countdown.WaitAsync(TimeSpan.FromSeconds(2));
                    Assert.Equal(0, countdown.CurrentCount);
                }
            }
        }

        [Fact]
        public async Task CanRunWorkItemJobUntilEmpty() {
            using (var queue = new InMemoryQueue<WorkItemData>(o => o.LoggerFactory(Log))) {
                using (var messageBus = new InMemoryMessageBus(o => o.LoggerFactory(Log))) {
                    var handlerRegistry = new WorkItemHandlers();
                    var job = new WorkItemJob(queue, messageBus, handlerRegistry, Log);

                    handlerRegistry.Register<MyWorkItem>(new MyWorkItemHandler(Log));

                    await queue.EnqueueAsync(new MyWorkItem {
                        SomeData = "Test"
                    }, true);

                    await queue.EnqueueAsync(new MyWorkItem {
                        SomeData = "Test"
                    }, true);

                    await job.RunUntilEmptyAsync();
                    var stats = await queue.GetQueueStatsAsync();
                    Assert.Equal(2, stats.Enqueued);
                    Assert.Equal(2, stats.Dequeued);
                    Assert.Equal(2, stats.Completed);
                }
            }
        }

        [Fact]
        public async Task CanRunBadWorkItem() {
            using (var queue = new InMemoryQueue<WorkItemData>(o => o.RetryDelay(TimeSpan.FromMilliseconds(500)).LoggerFactory(Log))) {
                using (var messageBus = new InMemoryMessageBus(o => o.LoggerFactory(Log))) {
                    var handlerRegistry = new WorkItemHandlers();
                    var job = new WorkItemJob(queue, messageBus, handlerRegistry, Log);

                    handlerRegistry.Register<MyWorkItem>(ctx => {
                        var jobData = ctx.GetData<MyWorkItem>();
                        Assert.Equal("Test", jobData.SomeData);
                        throw new Exception();
                    });

                    string jobId = await queue.EnqueueAsync(new MyWorkItem {
                        SomeData = "Test"
                    }, true);

                    var countdown = new AsyncCountdownEvent(1);
                    await messageBus.SubscribeAsync<WorkItemStatus>(status => {
                        if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Progress: {Progress}", status.Progress);
                        Assert.Equal(jobId, status.WorkItemId);
                        countdown.Signal();
                    });

                    await job.RunUntilEmptyAsync();
                    await countdown.WaitAsync(TimeSpan.FromSeconds(2));
                    Assert.Equal(0, countdown.CurrentCount);
                }
            }
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
                await SystemClock.SleepAsync(100);
                await context.ReportProgressAsync(10 * i);
            }
        }
    }
}