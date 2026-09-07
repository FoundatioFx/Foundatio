using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Foundatio.Tests.Messaging;

public class FailureHandlingTests
{
    [Fact]
    public async Task ConsumeAsync_SettledHandlerCleanup_DoesNotHoldConsumerCapacity()
    {
        var token = TestContext.Current.CancellationToken;
        await using var transport = new InMemoryMessageTransport();
        await using var bus = new MessageBus(transport);
        using var finishCleanup = new ManualResetEventSlim();
        var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var third = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        await using var subscription = await bus.ConsumeAsync<FailingItem>((_, ct) =>
        {
            int call = Interlocked.Increment(ref calls);
            if (call == 1)
                ct.Register(() => { cleanupStarted.TrySetResult(); finishCleanup.Wait(token); });
            else if (call == 2) second.TrySetResult();
            else third.TrySetResult();
            return Task.CompletedTask;
        }, cancellationToken: token);
        try
        {
            await bus.SendBatchAsync(new[] { new FailingItem(), new FailingItem(), new FailingItem() }, cancellationToken: token);
            await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            await second.Task.WaitAsync(TimeSpan.FromSeconds(1), token);
            await Task.Delay(100, token);
            Assert.False(third.Task.IsCompleted);
        }
        finally { finishCleanup.Set(); }
        await third.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
    }

    [Fact]
    public async Task ConsumeAsync_ConcurrentPulls_SharesCapacityAndCancelsPendingRequests()
    {
        var token = TestContext.Current.CancellationToken;
        var transport = new Mock<ISupportsPull>();
        var info = transport.As<ITransportInfo>();
        info.SetupGet(t => t.SupportedRoles).Returns(new HashSet<DestinationRole> { DestinationRole.Queue });
        info.Setup(t => t.GetCapabilities(It.IsAny<DestinationAddress>())).Returns(new TransportCapabilities
        {
            MaxReceiveBatchSize = 2,
            MaxConcurrentReceives = 4
        });
        var full = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int reserved = 0;
        int calls = 0;
        int cancelled = 0;
        transport.Setup(t => t.ReceiveAsync(It.IsAny<DestinationAddress>(), It.IsAny<ReceiveRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async (DestinationAddress _, ReceiveRequest request, CancellationToken ct) =>
            {
                Assert.InRange(request.MaxMessages, 1, 2);
                Interlocked.Increment(ref calls);
                int total = Interlocked.Add(ref reserved, request.MaxMessages);
                Assert.InRange(total, 1, 5);
                if (total == 5) full.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                finally { Interlocked.Increment(ref cancelled); }
                return (IReadOnlyList<TransportEntry>)Array.Empty<TransportEntry>();
            });
        await using var bus = new MessageBus(transport.Object);
        var subscription = await bus.ConsumeAsync((_, _) => Task.CompletedTask,
            new MessageConsumerOptions { Destination = "work", MaxConcurrency = 5 }, token);
        try
        {
            await full.Task.WaitAsync(TimeSpan.FromSeconds(2), token);
            Assert.Equal(3, Volatile.Read(ref calls));
        }
        finally { await subscription.DisposeAsync(); }
        Assert.Equal(3, Volatile.Read(ref cancelled));
    }

    [Fact]
    public async Task ConsumeAsync_BatchCapacityReturns_ReceivesWithoutWaitingForCollectionTimeout()
    {
        var token = TestContext.Current.CancellationToken;
        var transport = new Mock<ISupportsPull>();
        var info = transport.As<ITransportInfo>();
        info.SetupGet(t => t.SupportedRoles).Returns(new HashSet<DestinationRole> { DestinationRole.Queue });
        info.Setup(t => t.GetCapabilities(It.IsAny<DestinationAddress>())).Returns(new TransportCapabilities
        {
            MaxReceiveBatchSize = 2,
            ReceiveBatchDelay = TimeSpan.FromSeconds(10)
        });
        var contexts = new ConcurrentDictionary<string, IMessageContext>();
        var full = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int next = 0;
        transport.Setup(t => t.CompleteAsync(It.IsAny<TransportEntry>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        transport.Setup(t => t.ReceiveAsync(It.IsAny<DestinationAddress>(), It.IsAny<ReceiveRequest>(), It.IsAny<CancellationToken>()))
            .Returns((DestinationAddress source, ReceiveRequest request, CancellationToken _) =>
            {
                var entries = new List<TransportEntry>();
                for (int i = 0; i < request.MaxMessages && next < 4; i++)
                    entries.Add(new TransportEntry { Id = (++next).ToString(), Destination = source, Body = ReadOnlyMemory<byte>.Empty, Receipt = default });
                return Task.FromResult<IReadOnlyList<TransportEntry>>(entries);
            });
        await using var bus = new MessageBus(transport.Object);
        await using var subscription = await bus.ConsumeAsync((context, _) =>
        {
            contexts[context.BrokerMessageId] = context;
            if (context.BrokerMessageId == "2") full.TrySetResult();
            if (context.BrokerMessageId == "4") replacement.TrySetResult();
            return Task.CompletedTask;
        }, new MessageConsumerOptions { Destination = "work", MaxConcurrency = 2, AckMode = AckMode.Manual }, token);
        await full.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
        await contexts["1"].CompleteAsync(token);
        await Task.Delay(100, token);
        Assert.False(replacement.Task.IsCompleted);
        await contexts["2"].CompleteAsync(token);
        await replacement.Task.WaitAsync(TimeSpan.FromSeconds(1), token);
        foreach (var context in contexts.Values) await context.CompleteAsync(token);
    }

    [Fact]
    public async Task ConsumeAsync_BatchedPulls_FillsConcurrencyAndDoesNotWaitForSlowHandler()
    {
        var token = TestContext.Current.CancellationToken;
        var transport = new Mock<ISupportsPull>();
        var info = transport.As<ITransportInfo>();
        info.SetupGet(t => t.SupportedRoles).Returns(new HashSet<DestinationRole> { DestinationRole.Queue });
        info.Setup(t => t.GetCapabilities(It.IsAny<DestinationAddress>())).Returns(new TransportCapabilities
        {
            MaxReceiveBatchSize = 2,
            ReceiveBatchDelay = TimeSpan.FromMilliseconds(1)
        });
        var contexts = new ConcurrentDictionary<string, IMessageContext>();
        var full = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int next = 0;
        transport.Setup(t => t.CompleteAsync(It.IsAny<TransportEntry>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        transport.Setup(t => t.ReceiveAsync(It.IsAny<DestinationAddress>(), It.IsAny<ReceiveRequest>(), It.IsAny<CancellationToken>()))
            .Returns((DestinationAddress source, ReceiveRequest request, CancellationToken _) =>
            {
                Assert.InRange(request.MaxMessages, 1, 2);
                var entries = new List<TransportEntry>();
                for (int i = 0; i < request.MaxMessages && next < 9; i++)
                    entries.Add(new TransportEntry { Id = (++next).ToString(), Destination = source, Body = ReadOnlyMemory<byte>.Empty, Receipt = default });
                return Task.FromResult<IReadOnlyList<TransportEntry>>(entries);
            });
        await using var bus = new MessageBus(transport.Object);
        await using var subscription = await bus.ConsumeAsync((context, _) =>
        {
            contexts[context.BrokerMessageId] = context;
            if (contexts.Count == 8) full.TrySetResult();
            if (context.BrokerMessageId == "9") replacement.TrySetResult();
            return Task.CompletedTask;
        }, new MessageConsumerOptions { Destination = "work", MaxConcurrency = 8, AckMode = AckMode.Manual }, token);
        await full.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
        Assert.Equal(8, next);
        Assert.False(replacement.Task.IsCompleted);
        await contexts["2"].CompleteAsync(token);
        await replacement.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
        Assert.False(contexts["1"].IsHandled);
        foreach (var context in contexts.Values)
            await context.CompleteAsync(token);
    }

    [Fact]
    public async Task ConsumeAsync_TransportReceiveLimit_CapsEachPull()
    {
        var token = TestContext.Current.CancellationToken;
        var transport = new Mock<ISupportsPull>();
        var info = transport.As<ITransportInfo>();
        info.SetupGet(t => t.SupportedRoles).Returns(new HashSet<DestinationRole> { DestinationRole.Queue });
        info.Setup(t => t.GetCapabilities(It.IsAny<DestinationAddress>())).Returns(new TransportCapabilities { MaxReceiveBatchSize = 2 });
        var received = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.Setup(t => t.ReceiveAsync(It.IsAny<DestinationAddress>(), It.IsAny<ReceiveRequest>(), It.IsAny<CancellationToken>()))
            .Returns((DestinationAddress _, ReceiveRequest request, CancellationToken _) =>
            {
                received.TrySetResult(request.MaxMessages);
                return Task.FromResult<IReadOnlyList<TransportEntry>>(Array.Empty<TransportEntry>());
            });
        await using var bus = new MessageBus(transport.Object);
        await using var subscription = await bus.ConsumeAsync((_, _) => Task.CompletedTask,
            new MessageConsumerOptions { Destination = "work", MaxConcurrency = 8 }, token);
        Assert.Equal(2, await received.Task.WaitAsync(TimeSpan.FromSeconds(5), token));
    }

    [Theory]
    [InlineData(TopologyMode.Ensure)]
    [InlineData(TopologyMode.Validate)]
    public async Task DeadLetterFallback_HonorsTopologyBeforeSendingAndCompletingAsync(TopologyMode mode)
    {
        var token = TestContext.Current.CancellationToken;
        var transport = new Mock<ISupportsProvisioning>();
        var entry = new TransportEntry { Id = "source", Destination = DestinationAddress.ForQueue("work"), Body = new byte[] { 1 }, Receipt = default };
        transport.Setup(t => t.SendAsync(It.IsAny<DestinationAddress>(), It.IsAny<IReadOnlyList<TransportMessage>>(), It.IsAny<TransportSendOptions>(), token))
            .ReturnsAsync(new SendResult { Items = new[] { new SendItemResult { MessageId = "parked" } } });
        if (mode == TopologyMode.Validate)
        {
            await Assert.ThrowsAsync<MessageBusException>(() => MessageContext.DeadLetterAsync(transport.Object, entry, "failure", null,
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, token, mode));
            transport.Verify(t => t.SendAsync(It.IsAny<DestinationAddress>(), It.IsAny<IReadOnlyList<TransportMessage>>(), It.IsAny<TransportSendOptions>(), token), Times.Never);
            transport.Verify(t => t.CompleteAsync(entry, token), Times.Never);
        }
        else
        {
            await MessageContext.DeadLetterAsync(transport.Object, entry, "failure", null, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, token, mode);
            transport.Verify(t => t.EnsureAsync(It.Is<IReadOnlyList<DestinationDeclaration>>(d => d[0].Address.Name == "work.deadletter"), token), Times.Once);
            transport.Verify(t => t.CompleteAsync(entry, token), Times.Once);
        }
    }

    [Fact]
    public async Task ConsumeAsync_WithManualAcknowledgement_HoldsConcurrencySlotUntilSettlementAsync()
    {
        var token = TestContext.Current.CancellationToken;
        await using var transport = new InMemoryMessageTransport();
        await using var bus = new MessageBus(transport);
        var first = new TaskCompletionSource<IMessageContext<FailingItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<IMessageContext<FailingItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
        int count = 0;
        await using var consumer = await bus.ConsumeAsync<FailingItem>((context, _) =>
        {
            (Interlocked.Increment(ref count) == 1 ? first : second).TrySetResult(context);
            return Task.CompletedTask;
        }, new MessageConsumerOptions { AckMode = AckMode.Manual }, token);
        await bus.SendBatchAsync(new[] { new FailingItem(), new FailingItem() }, cancellationToken: token);
        var message = await first.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
        await Task.Delay(100, token);
        Assert.False(second.Task.IsCompleted);
        await message.CompleteAsync(token);
        var next = await second.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
        await next.CompleteAsync(token);
    }

    [Fact]
    public async Task ConsumeAsync_WhenLeaseRenewalFails_CancelsHandlerWithoutSettlingAsync()
    {
        var time = new FakeTimeProvider();
        var transport = new Mock<ISupportsPull>();
        var renewal = transport.As<ISupportsLockRenewal>();
        var entry = new TransportEntry
        {
            Id = "leased-message",
            Destination = DestinationAddress.ForQueue("work"),
            Body = new byte[] { 1 },
            LockExpiresUtc = time.GetUtcNow().AddSeconds(10),
            Receipt = default
        };
        transport.SetupSequence(t => t.ReceiveAsync(It.IsAny<DestinationAddress>(), It.IsAny<ReceiveRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { entry })
            .ReturnsAsync(Array.Empty<TransportEntry>());
        renewal.Setup(t => t.RenewLockAsync(entry, It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ReceiptExpiredException());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var bus = new MessageBus(transport.Object, new MessageBusOptions { TimeProvider = time });
        await using var consumer = await bus.ConsumeAsync(async (context, token) =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                Assert.True(context.CancellationToken.IsCancellationRequested);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context.CompleteAsync(TestContext.Current.CancellationToken));
                cancelled.TrySetResult();
            }
        }, new MessageConsumerOptions { Destination = "work" }, TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(6));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        transport.Verify(t => t.CompleteAsync(entry, It.IsAny<CancellationToken>()), Times.Never);
        transport.Verify(t => t.AbandonAsync(entry, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CompleteAsync_WhenTransportFails_RemainsUnsettledAndCanRetryAsync()
    {
        var token = TestContext.Current.CancellationToken;
        var transport = new Mock<IMessageTransport>();
        var entry = new TransportEntry { Id = "message", Destination = DestinationAddress.ForQueue("work"), Body = new byte[] { 1 }, Receipt = default };
        transport.SetupSequence(t => t.CompleteAsync(entry, token))
            .ThrowsAsync(new TimeoutException("broker unavailable"))
            .Returns(Task.CompletedTask);
        var context = new MessageContext(transport.Object, entry, token);
        await Assert.ThrowsAsync<TimeoutException>(() => context.CompleteAsync(token));
        Assert.False(context.IsHandled);
        await context.CompleteAsync(token);
        Assert.True(context.IsHandled);
        await context.CompleteAsync(token);
        transport.Verify(t => t.CompleteAsync(entry, token), Times.Exactly(2));
    }

    [Fact]
    public async Task RejectAsync_WhenDeadLetterStorageFails_DoesNotDeleteOriginalAsync()
    {
        var token = TestContext.Current.CancellationToken;
        var transport = new Mock<IMessageTransport>();
        var entry = new TransportEntry { Id = "message", Destination = DestinationAddress.ForQueue("work"), Body = new byte[] { 1 }, Receipt = default };
        transport.Setup(t => t.SendAsync(It.IsAny<DestinationAddress>(), It.IsAny<IReadOnlyList<TransportMessage>>(), It.IsAny<TransportSendOptions>(), token))
            .ThrowsAsync(new TimeoutException("DLQ unavailable"));
        var context = new MessageContext(transport.Object, entry, token);
        await Assert.ThrowsAsync<TimeoutException>(() => context.RejectAsync(new() { Terminal = true }, token));
        transport.Verify(t => t.CompleteAsync(entry, token), Times.Never);
        Assert.False(context.IsHandled);
    }

    [Fact]
    public async Task DeadLetterOn_MatchingException_DeadLettersOnFirstAttemptAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var transport = new InMemoryMessageTransport();
        await using var bus = new MessageBus(transport);
        int attempts = 0;

        var options = new MessageConsumerOptions { MaxAttempts = 5 };
        options.DeadLetterOn<ArgumentException>();

        await using var subscription = await bus.ConsumeAsync<FailingItem>((_, _) =>
        {
            Interlocked.Increment(ref attempts);
            throw new ArgumentException("bad data a retry can never fix");
        }, options, cancellationToken);

        await bus.SendAsync(new FailingItem { Data = "poison" }, cancellationToken: cancellationToken);

        var stats = await WaitForDeadLetterAsync(transport, "failing-item", cancellationToken);
        Assert.Equal(1, stats.Deadletter);
        Assert.Equal(1, Volatile.Read(ref attempts)); // never retried

        var dead = Assert.Single(await transport.PeekDeadLetteredAsync(DestinationAddress.ForQueue("failing-item"), new DeadLetterQuery { Limit = 10 }, cancellationToken));
        Assert.Equal("unrecoverable:ArgumentException", dead.Headers[KnownHeaders.DeadLetterReason]);
    }

    [Fact]
    public async Task DeadLetterWhen_GlobalPolicy_AppliesWhenSubscriptionDoesNotOverrideAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var transport = new InMemoryMessageTransport();
        await using var bus = new MessageBus(transport, new MessageBusOptions
        {
            RetryPolicy = new RetryPolicy { DeadLetterWhen = ex => ex is InvalidOperationException }
        });
        int attempts = 0;

        await using var subscription = await bus.ConsumeAsync<FailingItem>((_, _) =>
        {
            Interlocked.Increment(ref attempts);
            throw new InvalidOperationException("unrecoverable per global policy");
        }, cancellationToken: cancellationToken);

        await bus.SendAsync(new FailingItem { Data = "poison" }, cancellationToken: cancellationToken);

        var stats = await WaitForDeadLetterAsync(transport, "failing-item", cancellationToken);
        Assert.Equal(1, stats.Deadletter);
        Assert.Equal(1, Volatile.Read(ref attempts));
    }

    [Fact]
    public async Task DeadLetter_StampsForensicsHeadersAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var transport = new InMemoryMessageTransport();
        await using var bus = new MessageBus(transport);

        await using var subscription = await bus.ConsumeAsync<FailingItem>((_, _) =>
            throw new InvalidOperationException("the failure detail"),
            new MessageConsumerOptions { MaxAttempts = 1 }, cancellationToken);

        await bus.SendAsync(new FailingItem { Data = "doomed" }, cancellationToken: cancellationToken);

        await WaitForDeadLetterAsync(transport, "failing-item", cancellationToken);
        var dead = Assert.Single(await transport.PeekDeadLetteredAsync(DestinationAddress.ForQueue("failing-item"), new DeadLetterQuery { Limit = 10 }, cancellationToken));

        Assert.Equal(typeof(InvalidOperationException).FullName, dead.Headers[KnownHeaders.DeadLetterExceptionType]);
        Assert.Equal("the failure detail", dead.Headers[KnownHeaders.DeadLetterExceptionMessage]);
        Assert.NotEmpty(dead.Headers[KnownHeaders.DeadLetterExceptionStackTrace]);
        Assert.Equal("failing-item", dead.Headers[KnownHeaders.DeadLetterOriginalDestination]);
        Assert.NotEmpty(dead.Headers[KnownHeaders.DeadLetterFailedAt]);
        // The exhausted count is forensics-only: message.attempts is left alone so a replayed message starts fresh.
        Assert.Equal("1", dead.Headers[KnownHeaders.DeadLetterAttempts]);
        Assert.False(dead.Headers.ContainsKey(KnownHeaders.Attempts));
    }

    [Fact]
    public void DefaultBackoff_MatchesTheConvergedCurve()
    {
        // Immediate first retry, then 10s/20s/30s (capped) with ±20% jitter.
        Assert.Equal(TimeSpan.Zero, RetryPolicy.DefaultBackoff(1));

        foreach ((int attempt, double expectedSeconds) in new[] { (2, 10d), (3, 20d), (4, 30d), (7, 30d) })
        {
            var delay = RetryPolicy.DefaultBackoff(attempt);
            Assert.InRange(delay.TotalSeconds, expectedSeconds * 0.8, expectedSeconds * 1.2);
        }

        // The default policy uses the curve.
        Assert.Same(RetryPolicy.DefaultBackoff, new RetryPolicy().Backoff);
    }

    private static async Task<MessageDestinationStats> WaitForDeadLetterAsync(InMemoryMessageTransport transport, string destination, CancellationToken cancellationToken)
    {
        var address = DestinationAddress.ForQueue(destination);
        var stats = await transport.GetStatsAsync(address, cancellationToken);
        long deadline = Environment.TickCount64 + 10_000;
        while (stats.Deadletter == 0 && Environment.TickCount64 < deadline)
        {
            await Task.Delay(25, cancellationToken);
            stats = await transport.GetStatsAsync(address, cancellationToken);
        }

        return stats;
    }

    [MessageRoute("failing-item")]
    private sealed class FailingItem
    {
        public string? Data { get; set; }
    }
}
