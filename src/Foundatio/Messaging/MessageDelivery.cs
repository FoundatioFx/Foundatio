using System;
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
    private CancellationTokenSource? _handlerCancellation;
    private int _leaseLost;

    public MessageDelivery(IMessageReceiver receiver, IncomingMessage message, LeaseRenewer renewer)
    {
        Receiver = receiver;
        Message = message;
        _renewer = renewer;
    }

    public IMessageReceiver Receiver { get; }

    public IncomingMessage Message { get; }

    /// <summary>True once a renewal reported the lease lost: another consumer may have the message, so it must not be settled.</summary>
    public bool IsLeaseLost => Volatile.Read(ref _leaseLost) == 1;

    // Renewal state, read and written only under the renewer's lock.
    internal bool Tracked { get; set; }
    internal DateTimeOffset LeaseExpiresAt { get; set; }
    internal DateTimeOffset RenewAt { get; set; }
    internal int RenewalFailures { get; set; }
    internal Task? Renewal { get; set; }
    internal CancellationTokenSource? RenewalCancellation { get; set; }

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
    /// Stops renewing, then settles on a token that is never the handler's. Returns null when it settled, or the error,
    /// which is logged: the transport delivers the message again.
    /// </summary>
    public async Task<Exception?> SettleAsync(Func<IMessageReceiver, IncomingMessage, CancellationToken, Task> settle, string operation, ILogger logger)
    {
        await StopRenewingAsync().AnyContext();
        using var timeout = new CancellationTokenSource(SettleTimeout);
        try
        {
            await settle(Receiver, Message, timeout.Token).AnyContext();
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
