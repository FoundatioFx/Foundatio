using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Foundatio.Tests.Messaging;

public class MessageDeliveryLeaseTests
{
    [Fact]
    public async Task SettledBeforeRenewal_DoesNotRenewOrCancelProcessing()
    {
        var time = new FakeTimeProvider();
        var transport = new Mock<ISupportsLockRenewal>();
        using var processing = new CancellationTokenSource();
        await using var lease = CreateLease(transport.Object, time, processing);
        lease.Settled();
        time.Advance(TimeSpan.FromMinutes(2));
        await lease.Completion.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(processing.IsCancellationRequested);
        Assert.False(lease.IsLost);
        transport.Verify(t => t.RenewLockAsync(It.IsAny<TransportEntry>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TransientRenewalFailure_RetriesInsideTheOriginalLease()
    {
        var time = new FakeTimeProvider();
        var transport = new Mock<ISupportsLockRenewal>();
        var renewed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        transport.Setup(t => t.RenewLockAsync(It.IsAny<TransportEntry>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                if (Interlocked.Increment(ref calls) == 1) throw new TimeoutException("Transient broker failure");
                renewed.TrySetResult();
                return Task.CompletedTask;
            });
        using var processing = new CancellationTokenSource();
        await using var lease = CreateLease(transport.Object, time, processing);
        // Advance one tick at a time until the retry has completed; monitor continuations run asynchronously.
        for (int tick = 0; tick < 9 && !renewed.Task.IsCompleted; tick++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        await renewed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        lease.Settled();
        Assert.False(processing.IsCancellationRequested);
        Assert.True(calls >= 2);
    }

    [Fact]
    public async Task RenewalThatIgnoresCancellation_DoesNotPreventDisposal()
    {
        var time = new FakeTimeProvider();
        var transport = new Mock<ISupportsLockRenewal>();
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var broker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.Setup(t => t.RenewLockAsync(It.IsAny<TransportEntry>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .Returns((TransportEntry _, TimeSpan? _, CancellationToken ct) => { started.TrySetResult(ct); return broker.Task; });
        using var processing = new CancellationTokenSource();
        var lease = CreateLease(transport.Object, time, processing);
        try
        {
            time.Advance(TimeSpan.FromSeconds(5));
            var renewalToken = await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await lease.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.True(renewalToken.IsCancellationRequested);
            Assert.False(processing.IsCancellationRequested);
        }
        finally { broker.TrySetResult(); }
    }

    [Fact]
    public async Task SettlementRacingTheFirstLeaseCheck_DoesNotLeaveRenewalRunning()
    {
        for (int iteration = 0; iteration < 100; iteration++)
        {
            var time = new FakeTimeProvider();
            var transport = new Mock<ISupportsLockRenewal>();
            int calls = 0;
            transport.Setup(t => t.RenewLockAsync(It.IsAny<TransportEntry>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
                .Returns(() => { Interlocked.Increment(ref calls); return Task.CompletedTask; });
            using var processing = new CancellationTokenSource();
            var lease = CreateLease(transport.Object, time, processing);
            await Task.WhenAll(Task.Run(() => time.Advance(TimeSpan.FromSeconds(5)), TestContext.Current.CancellationToken),
                Task.Run(async () => await lease.DisposeAsync(), TestContext.Current.CancellationToken));
            int callsAtDisposal = calls;
            time.Advance(TimeSpan.FromMinutes(2));
            Assert.Equal(callsAtDisposal, calls);
            Assert.False(processing.IsCancellationRequested);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeadlineExpires_CancelsProcessingEvenWhenRenewalCannotFinish(bool autoRenew)
    {
        var time = new FakeTimeProvider();
        var transport = new Mock<ISupportsLockRenewal>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var broker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.Setup(t => t.RenewLockAsync(It.IsAny<TransportEntry>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .Returns(() => { started.TrySetResult(); return broker.Task; });
        using var processing = new CancellationTokenSource();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = processing.Token.Register(() => cancelled.TrySetResult());
        await using var lease = CreateLease(transport.Object, time, processing, autoRenew);
        try
        {
            time.Advance(TimeSpan.FromSeconds(5));
            if (autoRenew) await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            time.Advance(TimeSpan.FromSeconds(6));
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.True(lease.IsLost);
        }
        finally { broker.TrySetResult(); }
    }

    private static MessageDeliveryLease CreateLease(IMessageTransport transport, FakeTimeProvider time, CancellationTokenSource processing, bool autoRenew = true)
        => new(transport, new TransportEntry
        {
            Id = "work", Destination = DestinationAddress.ForQueue("work"), Body = new byte[] { 1 }, Receipt = default,
            LockExpiresUtc = time.GetUtcNow().AddSeconds(10)
        }, TimeSpan.FromSeconds(10), autoRenew, time, NullLogger.Instance, processing);
}
