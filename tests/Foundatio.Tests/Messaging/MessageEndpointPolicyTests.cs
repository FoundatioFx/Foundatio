using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Foundatio.Jobs;
using Moq;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Foundatio.Tests.Messaging;

public class MessageEndpointPolicyTests
{
    [Fact]
    public async Task ConcurrentExplicitCompletion_SettlesTheDeliveryOnce()
    {
        var token = TestContext.Current.CancellationToken;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivery = new Mock<IMessageContext>();
        delivery.SetupGet(value => value.Headers).Returns(MessageHeaders.Empty);
        delivery.Setup(value => value.CompleteAsync(It.IsAny<CancellationToken>())).Returns(release.Task);
        var pipeline = new MessageExecutionPipeline(new MessageExecutionOptions { QueueName = "exports" });
        await pipeline.ProcessAsync(delivery.Object, async (context, ct) =>
        {
            var first = context.CompleteAsync(ct);
            var second = context.CompleteAsync(ct);
            Assert.False(context.IsCompleted);
            release.TrySetResult();
            await Task.WhenAll(first, second);
            Assert.True(context.IsCompleted);
            await Assert.ThrowsAsync<InvalidOperationException>(() => context.AbandonAsync(ct));
            return MessageOutcome.Success;
        }, token);
        delivery.Verify(value => value.CompleteAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TrackedCancellation_PollsAndStopsTheRunningHandler()
    {
        var token = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        var store = new InMemoryJobRuntimeStore(time);
        await store.CreateIfAbsentAsync(new JobState { JobId = "cancel", Name = "exports", QueueName = "exports", ExecutionOwner = JobExecutionOwner.Broker }, token);
        var delivery = new Mock<IMessageContext>();
        delivery.SetupGet(value => value.Headers).Returns(MessageHeaders.Empty.ToBuilder().Set(ExecutionHeaders.ExecutionId, "cancel").Build());
        delivery.SetupGet(value => value.Attempts).Returns(1);
        delivery.Setup(value => value.CompleteAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = new MessageExecutionPipeline(new MessageExecutionOptions { QueueName = "exports", TrackProgress = true, CancellationPollInterval = TimeSpan.FromSeconds(1) }, store, time);
        var processing = pipeline.ProcessAsync(delivery.Object, async (_, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return MessageOutcome.Success;
        }, token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
        Assert.True(await store.RequestCancellationAsync("cancel", token));
        time.Advance(TimeSpan.FromSeconds(1));
        await processing.WaitAsync(TimeSpan.FromSeconds(5), token);
        Assert.Equal(JobStatus.Cancelled, (await store.GetAsync("cancel", token))!.Status);
        delivery.Verify(value => value.CompleteAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExpiredTrackingHistory_DoesNotPreventBrokerWorkFromRunning()
    {
        var store = new InMemoryJobRuntimeStore();
        var delivery = new Mock<IMessageContext>();
        delivery.SetupGet(value => value.Headers).Returns(MessageHeaders.Create(new Dictionary<string, string> { [ExecutionHeaders.ExecutionId] = "expired" }));
        delivery.SetupGet(value => value.Attempts).Returns(1);
        delivery.Setup(value => value.CompleteAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var pipeline = new MessageExecutionPipeline(new MessageExecutionOptions { QueueName = "exports", TrackProgress = true }, store);
        bool invoked = false;
        await pipeline.ProcessAsync(delivery.Object, (_, _) =>
        {
            invoked = true;
            return ValueTask.FromResult(MessageOutcome.Success);
        }, TestContext.Current.CancellationToken);
        Assert.True(invoked);
        delivery.Verify(value => value.CompleteAsync(It.IsAny<CancellationToken>()), Times.Once);
        Assert.Null(await store.GetAsync("expired", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ManualRenewal_ExtendsTheSupervisedDeadlineWhenAutoRenewIsDisabled()
    {
        var time = new FakeTimeProvider();
        await using var transport = new InMemoryMessageTransport(time);
        await using var bus = new MessageBus(transport, new MessageBusOptions { TimeProvider = time, OwnsTransport = false });
        var entered = new TaskCompletionSource<IMessageContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var consumer = await bus.ConsumeAsync(async (message, token) =>
        {
            entered.TrySetResult(message);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { cancelled.TrySetResult(); }
        }, new MessageConsumerOptions { Destination = "manual-lease", AutoRenewLock = false, VisibilityTimeout = TimeSpan.FromSeconds(30) }, TestContext.Current.CancellationToken);
        await bus.SendAsync("work", new MessageSendOptions { Destination = "manual-lease" }, TestContext.Current.CancellationToken);
        var delivery = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(10));
        await delivery.RenewLockAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(25));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(delivery.CancellationToken.IsCancellationRequested);
        time.Advance(TimeSpan.FromSeconds(40));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(delivery.IsLeaseLost);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedAcknowledgment_DoesNotPersistCompletion(bool cancellationRequested)
    {
        var store = new InMemoryJobRuntimeStore();
        await store.CreateIfAbsentAsync(new JobState
        {
            JobId = "ack-failure",
            Name = "exports",
            ExecutionOwner = JobExecutionOwner.Broker,
            QueueName = "exports",
            PayloadType = "Export",
            CancellationRequested = cancellationRequested,
            Status = JobStatus.Queued,
            CreatedUtc = DateTimeOffset.UtcNow,
            LastUpdatedUtc = DateTimeOffset.UtcNow
        }, cancellationToken: TestContext.Current.CancellationToken);
        var delivery = new Mock<IMessageContext>();
        delivery.SetupGet(value => value.Headers).Returns(MessageHeaders.Create(new Dictionary<string, string> { [ExecutionHeaders.ExecutionId] = "ack-failure" }));
        delivery.SetupGet(value => value.Attempts).Returns(1);
        delivery.Setup(value => value.CompleteAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new TimeoutException("Unknown broker acknowledgment"));
        var pipeline = new MessageExecutionPipeline(new MessageExecutionOptions { QueueName = "exports", TrackProgress = true }, store);
        await pipeline.ProcessAsync(delivery.Object, (_, _) => ValueTask.FromResult(MessageOutcome.Success), TestContext.Current.CancellationToken);
        Assert.Equal(JobStatus.RetryPending, (await store.GetAsync("ack-failure", TestContext.Current.CancellationToken))!.Status);
    }

    [Fact]
    public async Task ConsumeAsync_EndpointPolicy_ControlsVisibilityAndReceiveCapacity()
    {
        var token = TestContext.Current.CancellationToken;
        var transport = new Mock<ISupportsVisibilityTimeout>();
        transport.As<ISupportsPull>();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.Setup(t => t.ReceiveAsync(It.IsAny<DestinationAddress>(), It.IsAny<ReceiveRequest>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns(async (DestinationAddress source, ReceiveRequest request, TimeSpan visibility, CancellationToken ct) =>
            {
                Assert.Equal("exports", source.Name);
                Assert.InRange(request.MaxMessages, 1, 2);
                Assert.Equal(TimeSpan.FromSeconds(12), visibility);
                received.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return Array.Empty<TransportEntry>();
            });
        await using var bus = new MessageBus(transport.Object);
        await using var consumer = await bus.ConsumeAsync((_, _) => Task.CompletedTask, new MessageConsumerOptions
        {
            Destination = "exports",
            MaxConcurrency = 5,
            PrefetchCount = 2,
            VisibilityTimeout = TimeSpan.FromSeconds(12)
        }, token);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
    }

    [Fact]
    public async Task ConsumeAsync_GracefulShutdown_CompletesAdmittedWorkBeforeReturning()
    {
        var token = TestContext.Current.CancellationToken;
        await using var transport = new InMemoryMessageTransport();
        await using var bus = new MessageBus(transport);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken handlerToken = default;
        var consumer = await bus.ConsumeAsync<Work>(async (_, ct) =>
        {
            handlerToken = ct;
            started.TrySetResult();
            await finish.Task.WaitAsync(ct);
        }, new MessageConsumerOptions { Destination = "exports", ShutdownTimeout = TimeSpan.FromSeconds(5) }, token);
        try
        {
            await bus.SendAsync(new Work(), new MessageSendOptions { Destination = "exports" }, token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            var stopping = consumer.DisposeAsync().AsTask();
            Assert.False(handlerToken.IsCancellationRequested);
            Assert.False(stopping.IsCompleted);
            finish.TrySetResult();
            await stopping.WaitAsync(TimeSpan.FromSeconds(5), token);
            var stats = await transport.GetStatsAsync(DestinationAddress.ForQueue("exports"), token);
            Assert.Equal(1, stats.Completed);
            Assert.Equal(0, stats.Queued);
        }
        finally
        {
            finish.TrySetResult();
            await consumer.DisposeAsync();
        }
    }

    [Fact]
    public async Task ConsumeWithOutcomeAsync_RetryBudget_DeadLettersReturnedFailure()
    {
        var token = TestContext.Current.CancellationToken;
        await using var transport = new InMemoryMessageTransport();
        await using var bus = new MessageBus(transport);
        int calls = 0;
        await using var consumer = await bus.ConsumeWithOutcomeAsync<Work>((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return new ValueTask<MessageOutcome>(MessageOutcome.Retry("service unavailable"));
        }, new MessageConsumerOptions { Destination = "exports", MaxAttempts = 2, RedeliveryBackoff = _ => TimeSpan.Zero }, token);
        await bus.SendAsync(new Work(), new MessageSendOptions { Destination = "exports" }, token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        IReadOnlyList<TransportEntry> deadLetters;
        do
        {
            deadLetters = await transport.PeekDeadLetteredAsync(DestinationAddress.ForQueue("exports"), cancellationToken: deadline.Token);
            if (deadLetters.Count == 0) await Task.Delay(10, deadline.Token);
        } while (deadLetters.Count == 0);
        Assert.Equal(2, calls);
        Assert.Equal("service unavailable", Assert.Single(deadLetters).Headers[KnownHeaders.DeadLetterReason]);
    }

    private sealed record Work;
}
