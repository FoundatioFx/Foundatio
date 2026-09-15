using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Foundatio.Tests;

public class Review533RegressionTests
{
    [Fact]
    public async Task InMemoryConsumer_ConcurrencyTwo_StartsTwoHandlers()
    {
        var token = TestContext.Current.CancellationToken;
        await using var transport = new InMemoryMessageTransport();
        await using var bus = new MessageBus(transport, new MessageBusOptions { OwnsTransport = false });
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0;
        await bus.SendAsync(new Ping(), cancellationToken: token);
        await bus.SendAsync(new Ping(), cancellationToken: token);
        await using var consumer = await bus.ConsumeAsync<Ping>(async (_, ct) =>
        {
            Interlocked.Increment(ref started);
            firstStarted.TrySetResult();
            await release.Task.WaitAsync(ct);
        }, new MessageConsumerOptions { MaxConcurrency = 2 }, token);
        try
        {
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            await Task.Delay(200, token);
            Assert.Equal(2, Volatile.Read(ref started));
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task InMemoryConsumer_DisposedDuringProcessing_ReturnsUnfinishedMessage()
    {
        var token = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        await using var transport = new InMemoryMessageTransport(time);
        await using var bus = new MessageBus(transport, new MessageBusOptions { TimeProvider = time, OwnsTransport = false });
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await bus.SendAsync(new Ping(), cancellationToken: token);
        var consumer = await bus.ConsumeAsync<Ping>(async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }, cancellationToken: token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
        await consumer.DisposeAsync();
        time.Advance(TimeSpan.FromDays(1));
        await using var redelivered = await bus.ReceiveAsync<Ping>(new MessageReceiveOptions { WaitTime = TimeSpan.Zero }, token);
        Assert.NotNull(redelivered);
    }

    [Theory]
    [InlineData(JobStatus.Completed)]
    [InlineData(JobStatus.Failed)]
    [InlineData(JobStatus.Cancelled)]
    public async Task RegisteredJob_IsDisposedAfterExecution(JobStatus outcome)
    {
        var services = new ServiceCollection();
        var state = new DisposalState { Outcome = outcome };
        services.AddSingleton(state);
        services.AddFoundatio().Jobs.UseInMemory().AddJobType<DisposableJob>();
        await using var provider = services.BuildServiceProvider();
        var handle = await provider.GetRequiredService<IJobClient>().EnqueueAsync<DisposableJob>(new JobRequestOptions { MaxAttempts = 1 }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(await provider.GetRequiredService<IJobWorker>().RunAsync(handle.JobId, TestContext.Current.CancellationToken));
        Assert.Equal(outcome, (await handle.GetStateAsync(TestContext.Current.CancellationToken))!.Status);
        Assert.Equal(1, state.Disposed);
    }

    [Fact]
    public async Task ScheduledDispatch_LongBatch_RetiresAllSuccessfulSends()
    {
        var time = new FakeTimeProvider();
        var store = new InMemoryJobRuntimeStore(time);
        for (int i = 0; i < 4; i++)
            await store.ScheduleDispatchAsync(new ScheduledDispatchState
            {
                DispatchId = $"message-{i}",
                Destination = DestinationAddress.ForQueue("work"),
                DueUtc = time.GetUtcNow(),
                Body = "hello"u8.ToArray()
            }, TestContext.Current.CancellationToken);
        var transport = new Mock<IMessageTransport>();
        transport.Setup(t => t.SendAsync(It.IsAny<DestinationAddress>(), It.IsAny<IReadOnlyList<TransportMessage>>(), It.IsAny<TransportSendOptions>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                time.Advance(TimeSpan.FromSeconds(20));
                return Task.FromResult(new SendResult { Items = [new SendItemResult { MessageId = "sent" }] });
            });
        var dispatcher = new ScheduledMessageDispatcher(store, transport.Object, new ScheduledMessageDispatcherOptions { TimeProvider = time });
        Assert.Equal(4, await dispatcher.DispatchDueAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(await store.ClaimDueDispatchesAsync(time.GetUtcNow(), 10, "next-worker", TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ScheduledSend_SameApplicationIdAcrossDestinations_PreservesBothMessages()
    {
        var time = new FakeTimeProvider();
        var store = new InMemoryJobRuntimeStore(time);
        await using var transport = new InMemoryMessageTransport(time);
        await using var bus = new MessageBus(transport, new MessageBusOptions { RuntimeStore = store, TimeProvider = time, OwnsTransport = false });
        await bus.SendAsync(new Ping(), new MessageSendOptions { MessageId = "order-123", Destination = "billing", Delay = TimeSpan.FromMinutes(5) }, TestContext.Current.CancellationToken);
        await bus.SendAsync(new Ping(), new MessageSendOptions { MessageId = "order-123", Destination = "shipping", Delay = TimeSpan.FromMinutes(5) }, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(2, (await store.ClaimDueDispatchesAsync(time.GetUtcNow(), 10, "worker", TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken)).Count);
    }

    [Fact]
    public async Task JobWorker_IdleSlot_ProcessesNewJobWhileAnotherSlotIsBusy()
    {
        var services = new ServiceCollection();
        var state = new BlockingState();
        services.AddSingleton(state);
        services.AddFoundatio().Jobs.UseInMemory().AddJobType<BlockingJob>().AddJobType<QuickJob>();
        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IJobClient>();
        using var worker = new JobWorker(provider.GetRequiredService<IJobRuntimeStore>(), provider, new JobWorkerOptions { MaxConcurrency = 2 });
        await client.EnqueueAsync<BlockingJob>(cancellationToken: TestContext.Current.CancellationToken);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var batch = worker.RunContinuouslyAsync(stopping.Token);
        await state.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await client.EnqueueAsync<QuickJob>(cancellationToken: TestContext.Current.CancellationToken);
        try
        {
            await Task.Delay(200, TestContext.Current.CancellationToken);
            Assert.True(state.QuickStarted.Task.IsCompleted, "The second worker slot is idle, but cannot accept new work until the blocking job completes.");
        }
        finally
        {
            state.Release.TrySetResult();
            await stopping.CancelAsync();
            await batch.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    public sealed class DisposalState { public int Disposed; public JobStatus Outcome; }
    public sealed class DisposableJob(DisposalState state) : IJob, IAsyncDisposable
    {
        public Task<JobResult> RunAsync(JobExecutionContext context) => Task.FromResult(state.Outcome switch
        {
            JobStatus.Completed => JobResult.Success,
            JobStatus.Cancelled => JobResult.Cancelled,
            _ => JobResult.FromException(new InvalidOperationException("Expected job failure"))
        });
        public ValueTask DisposeAsync() { state.Disposed++; return ValueTask.CompletedTask; }
    }
    public sealed record Ping;
    public sealed class BlockingState
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource QuickStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    public sealed class BlockingJob(BlockingState state) : IJob
    {
        public async Task<JobResult> RunAsync(JobExecutionContext context)
        {
            state.Started.TrySetResult();
            await state.Release.Task.WaitAsync(context.CancellationToken);
            return JobResult.Success;
        }
    }
    public sealed class QuickJob(BlockingState state) : IJob
    {
        public Task<JobResult> RunAsync(JobExecutionContext context) { state.QuickStarted.TrySetResult(); return Task.FromResult(JobResult.Success); }
    }
}
