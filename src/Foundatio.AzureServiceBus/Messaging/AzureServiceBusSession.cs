using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Foundatio.Extensions;

namespace Foundatio.Messaging.Transport;

/// <summary>
/// A session an <see cref="AzureServiceBusSessionReceiver"/> holds, and what it holds of it: messages handed out and not
/// settled, messages received and not handed out yet (behind one that is not due, or waiting for a receive to ask for
/// them), and retries waiting out their delay. While a grouped message is unsettled or a retry waits, no later message
/// of the session is handed out or received, so its group stays in order. Messages without a group key share a few
/// sessions and are in no group, so more of a session are handed out and received while only those are unsettled.
/// </summary>
/// <remarks>
/// Service Bus locks a session, not its messages, so every message of the session shares <see cref="LeaseExpiresAt"/>.
/// The receiver renews the lock itself only while nothing it handed out is unsettled; otherwise Foundatio renews it
/// through the deliveries.
/// </remarks>
internal sealed class AzureServiceBusSession
{
    /// <summary>How long a session whose last receive came back short waits before it is received from again while its ungrouped messages are handled.</summary>
    private static readonly TimeSpan DrainedPollInterval = TimeSpan.FromSeconds(1);

    private readonly object _gate = new();
    private readonly Dictionary<ServiceBusReceivedMessage, bool> _handedOut = new(ReferenceEqualityComparer.Instance);
    private readonly List<ServiceBusReceivedMessage> _held = [];
    private readonly List<AzureServiceBusSessionRetry> _retries = [];
    private DateTimeOffset? _heldUntil;
    private int _groupedHandedOut;
    private bool _isNew = true;
    private int _receiving;
    private bool _drained;
    private bool _stale;
    private DateTimeOffset _drainedAt;
    private DateTimeOffset _leaseExpiresAt;
    private Task? _renewal;
    private bool _lost;

    public AzureServiceBusSession(ServiceBusSessionReceiver receiver, DateTimeOffset leaseExpiresAt)
    {
        Receiver = receiver;
        _leaseExpiresAt = leaseExpiresAt;
    }

    public ServiceBusSessionReceiver Receiver { get; }

    public string SessionId => Receiver.SessionId;

    /// <summary>True until the first receive from the session completes, which may take longer while its link opens.</summary>
    public bool IsNew
    {
        get { lock (_gate) return _isNew; }
    }

    /// <summary>When the session lock ends, on the transport's clock.</summary>
    public DateTimeOffset LeaseExpiresAt
    {
        get { lock (_gate) return _leaseExpiresAt; }
        set { lock (_gate) _leaseExpiresAt = value; }
    }

    public bool IsLost
    {
        get { lock (_gate) return _lost; }
    }

    /// <summary>Whether a renewal of the session lock is in flight.</summary>
    public bool IsRenewing
    {
        get { lock (_gate) return _renewal is { IsCompleted: false }; }
    }

    /// <summary>Held only for a message or retry that is not due: nothing handed out is unsettled and nothing is being received.</summary>
    public bool IsParked
    {
        get { lock (_gate) return _handedOut.Count == 0 && _receiving == 0 && (_retries.Count > 0 || _heldUntil is not null); }
    }

    /// <summary>Nothing it handed out is unsettled, so the receiver keeps its lock rather than Foundatio.</summary>
    public bool HasNothingHandedOut
    {
        get { lock (_gate) return _handedOut.Count == 0; }
    }

    /// <summary>
    /// Holds nothing that waits for a due time and nothing Foundatio is handling: what it has, if anything, waits only for
    /// a receive to ask for it, so the session can be closed when nothing does.
    /// </summary>
    public bool IsUnserved
    {
        get { lock (_gate) return _handedOut.Count == 0 && _receiving == 0 && _retries.Count == 0 && _heldUntil is null; }
    }

    /// <summary>
    /// The session should be closed: its last receive came back with fewer messages than it asked for and everything
    /// from it is settled, or a grouped message went back into it ahead of messages received after it, which closing
    /// releases behind it, now that nothing handed out is unsettled.
    /// </summary>
    public bool IsDone
    {
        get { lock (_gate) return (_drained && IsIdleCore) || (_stale && _handedOut.Count == 0 && _receiving == 0 && _retries.Count == 0); }
    }

    /// <summary>Whether a receive from the session is in flight.</summary>
    public bool IsReceiving
    {
        get { lock (_gate) return _receiving > 0; }
    }

    /// <summary>The earliest time a held message or a retry can be handed out or released, if any.</summary>
    public DateTimeOffset? NextDueAt
    {
        get
        {
            lock (_gate)
            {
                var next = !_stale && _groupedHandedOut == 0 && _retries.Count == 0 ? _heldUntil : null;
                foreach (var retry in _retries)
                {
                    if (!retry.Releasing && (next is null || retry.DueAt < next))
                        next = retry.DueAt;
                }

                return next;
            }
        }
    }

    /// <summary>When a session whose last receive came back short is received from again while its ungrouped messages are handled, if it is waiting for that.</summary>
    public DateTimeOffset? NextPollAt
    {
        get { lock (_gate) return _drained && !_lost && _receiving == 0 && _handedOut.Count > 0 && _groupedHandedOut == 0 && _held.Count == 0 && _retries.Count == 0 ? _drainedAt + DrainedPollInterval : null; }
    }

    public void MarkLost()
    {
        lock (_gate)
            _lost = true;
    }

    /// <summary>
    /// Whether the session's next messages should be received from Service Bus now: nothing grouped is unsettled, nothing
    /// is held or waiting to be retried, no receive is in flight, and the last receive did not come back short, unless
    /// it did a while ago and ungrouped messages are still being handled.
    /// </summary>
    public bool CanReceive(DateTimeOffset now)
    {
        lock (_gate)
        {
            return !_lost && !_stale && _receiving == 0 && _groupedHandedOut == 0 && _held.Count == 0 && _retries.Count == 0
                && (!_drained || (_handedOut.Count > 0 && now - _drainedAt >= DrainedPollInterval));
        }
    }

    /// <summary>Records a receive of up to <paramref name="maxMessages"/> going out, so no other starts until it ends.</summary>
    public void BeginReceive(int maxMessages)
    {
        lock (_gate)
            _receiving = maxMessages;
    }

    /// <summary>
    /// Records what a receive returned, in order, to hand out: the first message that is not due yet holds back every
    /// message after it until it is due.
    /// </summary>
    public void EndReceive(IReadOnlyList<ServiceBusReceivedMessage> received, DateTimeOffset now, Func<ServiceBusReceivedMessage, DateTimeOffset, bool> isEarly)
    {
        lock (_gate)
        {
            _drained = received.Count < _receiving;
            _drainedAt = now;
            _receiving = 0;
            _isNew = false;
            foreach (var message in received)
            {
                if (_held.Count == 0 && isEarly(message, now))
                    _heldUntil = AzureServiceBusMessageTransportCodec.GetDeliverAt(message);

                _held.Add(message);
            }
        }
    }

    /// <summary>Records a receive that failed, so the session can be received from again.</summary>
    public void EndReceive()
    {
        lock (_gate)
        {
            _receiving = 0;
            _isNew = false;
        }
    }

    /// <summary>
    /// Takes the held messages that can be handed out now, in order: none while a grouped message handed out is unsettled,
    /// a retry waits or an earlier message went back into the session, else from the first until <paramref name="max"/>
    /// or the next one that is not due.
    /// </summary>
    public IReadOnlyList<ServiceBusReceivedMessage> TakeDueHeld(DateTimeOffset now, int max, Func<ServiceBusReceivedMessage, DateTimeOffset, bool> isEarly)
    {
        lock (_gate)
        {
            if (_lost || _stale || _groupedHandedOut > 0 || _retries.Count > 0 || _held.Count == 0 || _heldUntil > now)
                return [];

            var due = new List<ServiceBusReceivedMessage>();
            while (_held.Count > 0 && due.Count < max && (due.Count == 0 || !isEarly(_held[0], now)))
            {
                due.Add(_held[0]);
                HandOut(_held[0]);
                _held.RemoveAt(0);
            }

            _heldUntil = _held.Count > 0 && isEarly(_held[0], now) ? AzureServiceBusMessageTransportCodec.GetDeliverAt(_held[0]) : null;
            return due;
        }
    }

    /// <summary>
    /// Forgets a handed-out message once it is settled; <paramref name="returned"/> is true when it went back to the
    /// session, which then has a message to receive. Returns false when it was not handed out (settled already).
    /// </summary>
    public bool Settle(ServiceBusReceivedMessage message, bool returned = false)
    {
        lock (_gate)
        {
            if (!_handedOut.TryGetValue(message, out bool grouped))
                return false;

            if (returned)
                Returned(grouped);

            return Forget(message);
        }
    }

    public bool IsHandedOut(ServiceBusReceivedMessage message)
    {
        lock (_gate)
            return _handedOut.ContainsKey(message);
    }

    /// <summary>Keeps a handed-out message locked until <paramref name="dueAt"/>, when it is abandoned so it is received again.</summary>
    public void AddRetry(ServiceBusReceivedMessage message, DateTimeOffset dueAt)
    {
        lock (_gate)
        {
            Forget(message);
            _retries.Add(new AzureServiceBusSessionRetry(message, dueAt));
        }
    }

    /// <summary>Takes the retries that are due and not already being released.</summary>
    public IReadOnlyList<AzureServiceBusSessionRetry> TakeDueRetries(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_retries.Count == 0)
                return [];

            var due = _retries.Where(r => r.DueAt <= now && !r.Releasing).ToList();
            foreach (var retry in due)
                retry.Releasing = true;

            return due;
        }
    }

    /// <summary>Forgets a retry once it was abandoned, so it is back in the session, or makes it due again when abandoning it failed.</summary>
    public void CompleteRetry(AzureServiceBusSessionRetry retry, bool released)
    {
        lock (_gate)
        {
            if (released)
            {
                _retries.Remove(retry);
                Returned(grouped: true);
            }
            else
            {
                retry.Releasing = false;
            }
        }
    }

    /// <summary>
    /// Everything to abandon when the session is closed: unsettled messages, and retries with their due times. Held
    /// messages are released by closing the session, which does not count a delivery.
    /// </summary>
    public (IReadOnlyList<ServiceBusReceivedMessage> HandedOut, IReadOnlyList<AzureServiceBusSessionRetry> Retries) TakeForRelease()
    {
        lock (_gate)
        {
            var handedOut = _handedOut.Keys.ToList();
            var retries = _retries.ToList();
            _handedOut.Clear();
            _groupedHandedOut = 0;
            _retries.Clear();
            _held.Clear();
            _heldUntil = null;
            _lost = true;
            return (handedOut, retries);
        }
    }

    /// <summary>
    /// Renews the session lock, sharing a renewal already in flight, and updates <see cref="LeaseExpiresAt"/> with the
    /// result of <paramref name="leaseEnd"/>.
    /// </summary>
    public Task RenewAsync(Func<DateTimeOffset> requestedAt, Func<DateTimeOffset, DateTimeOffset, DateTimeOffset> leaseEnd, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Task renewal;
        lock (_gate)
        {
            if (_renewal is null || _renewal.IsCompleted)
                _renewal = RenewOnceAsync(requestedAt(), leaseEnd, timeout);

            renewal = _renewal;
        }

        return renewal.WaitAsync(cancellationToken);
    }

    /// <summary>Nothing handed out, held, being received or waiting to be retried.</summary>
    private bool IsIdleCore => _handedOut.Count == 0 && _held.Count == 0 && _retries.Count == 0 && _receiving == 0;

    private async Task RenewOnceAsync(DateTimeOffset requestedAt, Func<DateTimeOffset, DateTimeOffset, DateTimeOffset> leaseEnd, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await Receiver.RenewSessionLockAsync(cancellation.Token).AnyContext();
        }
        catch (Exception ex) when (ex is ObjectDisposedException || (ex is ServiceBusException serviceBusException && AzureServiceBusMessageTransport.IsLockLost(serviceBusException)))
        {
            // The client closes a session receiver whose lock it lost.
            MarkLost();
            throw;
        }

        LeaseExpiresAt = leaseEnd(requestedAt, Receiver.SessionLockedUntil);
    }

    /// <summary>
    /// A message went back into the session, so there is something to receive. Grouped messages received after it and
    /// held here must not be handed out before it comes back.
    /// </summary>
    private void Returned(bool grouped)
    {
        _drained = false;
        if (grouped && _held.Count > 0)
            _stale = true;
    }

    private void HandOut(ServiceBusReceivedMessage message)
    {
        bool grouped = !AzureServiceBusMessageTransportCodec.IsUngrouped(message);
        _handedOut[message] = grouped;
        if (grouped)
            _groupedHandedOut++;
    }

    private bool Forget(ServiceBusReceivedMessage message)
    {
        if (!_handedOut.Remove(message, out bool grouped))
            return false;

        if (grouped)
            _groupedHandedOut--;

        return true;
    }
}

/// <summary>A message of a session waiting out its retry delay with the session locked.</summary>
internal sealed class AzureServiceBusSessionRetry(ServiceBusReceivedMessage message, DateTimeOffset dueAt)
{
    public ServiceBusReceivedMessage Message { get; } = message;
    public DateTimeOffset DueAt { get; } = dueAt;

    /// <summary>Set while it is being abandoned, so it is abandoned once.</summary>
    public bool Releasing { get; set; }
}
