using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging.Transport;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;

namespace Foundatio.Messaging;

/// <summary>
/// One received message and its lease. The processor's <see cref="LeaseRenewer"/> renews the lease from the moment the
/// message is received until it is settled, whether it is being handled or waiting behind an earlier message of its group.
/// </summary>
internal sealed class MessageDelivery
{
    /// <summary>How long a settlement may take. Settlement never uses the handler's token.</summary>
    public static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(15);

    private readonly LeaseRenewer _renewer;
    private readonly RetryHolds _holds;
    private readonly SettlementSlots _settlements;
    private readonly TimeProvider? _slotClock;
    private CancellationTokenSource? _handlerCancellation;
    private int _leaseLost;
    private DateTimeOffset _lostAt;
    private long _lostAtTicks;
    private int _settling;

    /// <param name="receiver">The receiver that received it.</param>
    /// <param name="message">The message.</param>
    /// <param name="renewer">Renews its lease until it is settled.</param>
    /// <param name="holds">Holds it for a retry the broker cannot delay.</param>
    /// <param name="settlements">Lets its settlement give the handler slot away.</param>
    /// <param name="slotClock">
    /// Set by a processor that reads ahead, which measures how long a delivery keeps its handler slot: records
    /// <see cref="HandlerReturnedAt"/> on it.
    /// </param>
    public MessageDelivery(IMessageReceiver receiver, IncomingMessage message, LeaseRenewer renewer, RetryHolds holds, SettlementSlots settlements, TimeProvider? slotClock = null)
    {
        Receiver = receiver;
        Message = message;
        _renewer = renewer;
        _holds = holds;
        _settlements = settlements;
        _slotClock = slotClock;
    }

    public IMessageReceiver Receiver { get; }

    /// <summary>Counts the deliveries of <see cref="Receiver"/> still in flight, so a failed receiver is disposed once they finish.</summary>
    internal StrongBox<int>? ReceiverInFlight { get; init; }

    public IncomingMessage Message { get; }

    /// <summary>True once a renewal reported the lease lost: another consumer may have the message, so it must not be settled.</summary>
    public bool IsLeaseLost => Volatile.Read(ref _leaseLost) == 1;

    /// <summary>True once <see cref="SettleAsync"/> has been called, whether or not the settlement succeeded.</summary>
    public bool IsSettling => Volatile.Read(ref _settling) == 1;

    // Renewal state, read and written only under the renewer's lock.
    internal bool Tracked { get; set; }
    internal DateTimeOffset LeaseExpiresAt { get; set; }
    internal DateTimeOffset LostAt
    {
        get => _lostAt;
        set
        {
            _lostAt = value;
            Volatile.Write(ref _lostAtTicks, value.UtcTicks);
        }
    }

    /// <summary><see cref="LostAt"/> in UTC ticks, readable without the renewer's lock.</summary>
    internal long LostAtTicks => Volatile.Read(ref _lostAtTicks);

    internal DateTimeOffset RenewAt { get; set; }
    internal int RenewalFailures { get; set; }
    internal Task? Renewal { get; set; }
    internal CancellationTokenSource? RenewalCancellation { get; set; }

    /// <summary>
    /// Set by the processor before the delivery is handled: the handler slots its sequence still holds, its own and one
    /// for each later message of its group in the batch.
    /// </summary>
    internal int SequenceSlots { get; set; }

    /// <summary>True once a retry hold gave the slots in <see cref="SequenceSlots"/> to other messages.</summary>
    internal bool SlotsReleased { get; set; }

    /// <summary>True once a settlement gave the delivery's own handler slot to other messages (see <see cref="SettlementSlots"/>).</summary>
    internal bool SettlesOffSlot { get; set; }

    /// <summary>
    /// Set by a processor that handles deliveries in order: completed once the handler has returned, when the delivery
    /// starts to settle or its handling ends, so the next delivery is handled while this one settles.
    /// </summary>
    internal TaskCompletionSource? HandlerReturned { get; set; }

    /// <summary>
    /// With a slot clock, its timestamp when the handler returned: when the delivery began to settle or was held for a
    /// retry, after which its handler slot may go to other messages. Zero until then, and without a slot clock.
    /// </summary>
    internal long HandlerReturnedAt { get; private set; }

    /// <summary>Cancels <paramref name="handlerCancellation"/> if the lease is lost while the handler runs.</summary>
    public void AttachHandler(CancellationTokenSource handlerCancellation)
    {
        Interlocked.Exchange(ref _handlerCancellation, handlerCancellation);
        if (IsLeaseLost)
            TryCancel(handlerCancellation);
    }

    internal void MarkLeaseLost()
    {
        Interlocked.Exchange(ref _leaseLost, 1);
        if (Volatile.Read(ref _handlerCancellation) is { } handlerCancellation)
            TryCancel(handlerCancellation);
    }

    /// <summary>Stops renewing and waits for a renewal in flight, so it cannot land after a settlement.</summary>
    public Task StopRenewingAsync() => _renewer.UntrackAsync(this);

    /// <summary>Renews again, starting at once, for example to hold the message in-process after a settlement failed.</summary>
    public void ResumeRenewing() => _renewer.Track(this, renewNow: true);

    /// <summary>
    /// Holds the message in-process for <paramref name="delay"/>, leased and renewed, for a retry the broker cannot
    /// delay; meanwhile its handler slot goes to other messages if the processor has room for another hold. Ends early
    /// when the lease is lost, when <paramref name="abortToken"/> is cancelled or when the processor stops.
    /// </summary>
    public Task HoldAsync(TimeSpan delay, CancellationToken abortToken)
    {
        MarkHandlerReturned();
        return _holds.HoldAsync(this, delay, abortToken);
    }

    /// <summary>
    /// Stops renewing, then settles on a token that is never the handler's; while either waits, the handler slot goes to
    /// other messages when the processor has room. Returns null when it settled, or the error, which is logged: the
    /// transport delivers the message again.
    /// </summary>
    public async Task<Exception?> SettleAsync(Func<IMessageReceiver, IncomingMessage, CancellationToken, Task> settle, string operation, ILogger logger)
    {
        Volatile.Write(ref _settling, 1);
        HandlerReturned?.TrySetResult();
        MarkHandlerReturned();
        var stopRenewing = StopRenewingAsync();
        if (!stopRenewing.IsCompleted)
        {
            _settlements.TryRelease(this);
            await stopRenewing.AnyContext();
        }

        // The timeout's timer starts only for a settlement that waits: one that completes at once, as an in-memory one
        // does, needs no timer, and keeps its slot, since giving it away would only cost.
        using var timeout = new CancellationTokenSource();
        try
        {
            var settling = settle(Receiver, Message, timeout.Token);
            if (!settling.IsCompleted)
            {
                timeout.CancelAfter(SettleTimeout);
                _settlements.TryRelease(this);
            }

            await settling.AnyContext();
            return null;
        }
        catch (MessageLeaseLostException ex)
        {
            logger.LogWarning("Could not {Operation} message {MessageId}: its lease was lost and it will be delivered again", operation, Message.MessageId);
            return ex;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not {Operation} message {MessageId}; the transport will deliver it again", operation, Message.MessageId);
            return ex;
        }
    }

    private void MarkHandlerReturned()
    {
        if (_slotClock is not null && HandlerReturnedAt == 0)
            HandlerReturnedAt = _slotClock.GetTimestamp();
    }

    private static void TryCancel(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
