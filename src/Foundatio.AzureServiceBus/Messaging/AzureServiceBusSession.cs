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
/// settled, messages received but held back behind one that is not due yet, and retries waiting out their delay.
/// While any of those exist no later message of the session is received, so its group stays in order. Messages without
/// a group key share a few sessions and are in no group, so more of a session are received while only those are unsettled.
/// </summary>
/// <remarks>
/// Service Bus locks a session, not its messages, so every message of the session shares <see cref="LeaseExpiresAt"/>.
/// The receiver renews the lock itself only while the session is <see cref="IsParked"/>: then nothing it handed out is
/// unsettled and Foundatio renews nothing.
/// </remarks>
internal sealed class AzureServiceBusSession
{
    private readonly object _gate = new();
    private readonly Dictionary<ServiceBusReceivedMessage, bool> _handedOut = new(ReferenceEqualityComparer.Instance);
    private readonly List<ServiceBusReceivedMessage> _held = [];
    private readonly List<AzureServiceBusSessionRetry> _retries = [];
    private DateTimeOffset? _heldUntil;
    private int _groupedHandedOut;
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

    /// <summary>True until the first receive from the session, which may take longer while its link opens.</summary>
    public bool IsNew { get; set; } = true;

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

    /// <summary>Held only for messages or retries that are not due: nothing handed out is unsettled.</summary>
    public bool IsParked
    {
        get { lock (_gate) return _handedOut.Count == 0 && (_held.Count > 0 || _retries.Count > 0); }
    }

    /// <summary>Nothing handed out, held or waiting to be retried, so the session can be closed.</summary>
    public bool IsIdle
    {
        get { lock (_gate) return _handedOut.Count == 0 && _held.Count == 0 && _retries.Count == 0; }
    }

    /// <summary>The earliest time a held message or a retry is due, if any.</summary>
    public DateTimeOffset? NextDueAt
    {
        get
        {
            lock (_gate)
            {
                var next = _heldUntil;
                foreach (var retry in _retries)
                {
                    if (next is null || retry.DueAt < next)
                        next = retry.DueAt;
                }

                return next;
            }
        }
    }

    public void MarkLost()
    {
        lock (_gate)
            _lost = true;
    }

    /// <summary>Whether the session's next messages can be received from Service Bus now.</summary>
    public bool CanReceive
    {
        get { lock (_gate) return !_lost && _groupedHandedOut == 0 && _held.Count == 0 && _retries.Count == 0; }
    }

    /// <summary>
    /// Takes the held messages that can be handed out now, in order: none while something handed out is unsettled or a
    /// retry waits, else from the first until <paramref name="max"/> or the next one that is not due.
    /// </summary>
    public IReadOnlyList<ServiceBusReceivedMessage> TakeDueHeld(DateTimeOffset now, int max, Func<ServiceBusReceivedMessage, DateTimeOffset, bool> isEarly)
    {
        lock (_gate)
        {
            if (_lost || _handedOut.Count > 0 || _retries.Count > 0 || _held.Count == 0 || _heldUntil > now)
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
    /// Records messages received from Service Bus, in order, and returns those to hand out: all of them up to the first
    /// that is not due yet, which is held with every message after it until it is due.
    /// </summary>
    public IReadOnlyList<ServiceBusReceivedMessage> Add(IReadOnlyList<ServiceBusReceivedMessage> received, DateTimeOffset now, Func<ServiceBusReceivedMessage, DateTimeOffset, bool> isEarly)
    {
        lock (_gate)
        {
            var handOut = new List<ServiceBusReceivedMessage>(received.Count);
            foreach (var message in received)
            {
                if (_held.Count == 0 && !isEarly(message, now))
                {
                    HandOut(message);
                    handOut.Add(message);
                    continue;
                }

                if (_held.Count == 0)
                    _heldUntil = AzureServiceBusMessageTransportCodec.GetDeliverAt(message);

                _held.Add(message);
            }

            return handOut;
        }
    }

    /// <summary>Forgets a handed-out message once it is settled. Returns false when it was not handed out (settled already).</summary>
    public bool Settle(ServiceBusReceivedMessage message)
    {
        lock (_gate)
            return Forget(message);
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
            var due = _retries.Where(r => r.DueAt <= now && !r.Releasing).ToList();
            foreach (var retry in due)
                retry.Releasing = true;

            return due;
        }
    }

    /// <summary>Forgets a retry once it was abandoned, or makes it due again when abandoning it failed.</summary>
    public void CompleteRetry(AzureServiceBusSessionRetry retry, bool released)
    {
        lock (_gate)
        {
            if (released)
                _retries.Remove(retry);
            else
                retry.Releasing = false;
        }
    }

    /// <summary>Everything that should be abandoned when the receiver closes: unsettled messages and retries. Held messages are released by closing the session, which does not count a delivery.</summary>
    public IReadOnlyList<ServiceBusReceivedMessage> TakeForRelease()
    {
        lock (_gate)
        {
            var release = _handedOut.Keys.Concat(_retries.Select(r => r.Message)).ToList();
            _handedOut.Clear();
            _groupedHandedOut = 0;
            _retries.Clear();
            _held.Clear();
            _heldUntil = null;
            _lost = true;
            return release;
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
