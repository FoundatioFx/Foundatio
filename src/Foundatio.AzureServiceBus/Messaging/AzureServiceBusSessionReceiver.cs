using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Foundatio.Extensions;
using Microsoft.Extensions.Logging;

namespace Foundatio.Messaging.Transport;

/// <summary>
/// Receives from a session-enabled queue or subscription. It accepts sessions as it needs them, up to
/// <see cref="AzureServiceBusMessageTransportOptions.MaxConcurrentSessions"/>, and receives from a session again only
/// once every grouped message it handed out of it is settled, so no receiver in any process gets a message of a group
/// while an earlier one is unsettled. Messages without a group key share a few sessions and are received from them
/// like a queue. A session with nothing more to receive is closed, so another receiver can take it.
/// </summary>
/// <remarks>
/// <para>
/// A message sent with a delay and a group key is in its session from the start, carrying its delivery time; the receiver
/// that gets it holds it, and every later message of the session it received with it, until it is due. A retry with a
/// delay is held the same way and abandoned when it is due, so it is received again first. Held sessions are renewed by
/// a background loop and do not count toward the session limit, up to <see cref="MaxParkedSessions"/>. A retry of a
/// message without a group key is a scheduled copy instead, as without sessions, so it holds nothing back.
/// </para>
/// <para>
/// Disposing the receiver abandons what it handed out and its retries, which counts a delivery, and closes its
/// sessions, which releases held messages without counting one.
/// </para>
/// </remarks>
internal sealed class AzureServiceBusSessionReceiver : IMessageReceiver
{
    internal const int MaxParkedSessions = 256;
    private static readonly TimeSpan NewSessionReceiveWait = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan HeldSessionReceiveWait = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan MinAcceptWait = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan BusyPollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan EarlyTolerance = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(10);

    private readonly AzureServiceBusMessageTransport _transport;
    private readonly AzureServiceBusReceiverEntity _entity;
    private readonly ServiceBusSessionReceiverOptions _sessionOptions = new() { ReceiveMode = ServiceBusReceiveMode.PeekLock, PrefetchCount = 0 };
    private readonly List<AzureServiceBusSession> _sessions = [];
    private readonly SemaphoreSlim _receiveLock = new(1, 1);
    private readonly CancellationTokenSource _disposing = new();
    private TimeSpan _lockDuration;
    private Task _maintenance = Task.CompletedTask;
    private int _disposed;

    public AzureServiceBusSessionReceiver(AzureServiceBusMessageTransport transport, MessageSource source, AzureServiceBusReceiverEntity entity)
    {
        _transport = transport;
        _entity = entity;
        Source = source;
        _lockDuration = entity.LockDuration ?? transport.Options.LockDuration;
    }

    public MessageSource Source { get; }

    public void Start() => _maintenance = Task.Run(MaintainAsync, CancellationToken.None);

    public async Task<IReadOnlyList<IncomingMessage>> ReceiveAsync(int maxMessages, TimeSpan maxWait, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxMessages, 1);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        int max = Math.Min(maxMessages, AzureServiceBusMessageTransport.MaxBatchSize);
        using var receiveCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposing.Token);
        var token = receiveCancellation.Token;
        long started = Stopwatch.GetTimestamp();
        try
        {
            await _receiveLock.WaitAsync(token).AnyContext();
        }
        catch (OperationCanceledException) when (_disposing.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return [];
        }

        try
        {
            while (true)
            {
                var messages = new List<IncomingMessage>();
                await CollectAsync(messages, max, token).AnyContext();
                if (messages.Count > 0)
                    return messages;

                var remaining = maxWait - Stopwatch.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero)
                    return [];

                // Wait for a new session, but no longer than until a held message or retry is due.
                var wait = remaining;
                if (NextDueAt() is { } dueAt && dueAt - _transport.TimeProvider.GetUtcNow() is var untilDue && untilDue < wait)
                    wait = untilDue > MinAcceptWait ? untilDue : MinAcceptWait;

                var (handling, parked) = CountSessions();
                if (handling < _transport.Options.MaxConcurrentSessions && parked < MaxParkedSessions)
                {
                    if (await AcceptAsync(wait > MinAcceptWait ? wait : MinAcceptWait, token).AnyContext() is { } session)
                    {
                        lock (_sessions)
                            _sessions.Add(session);
                    }
                }
                else
                {
                    await Task.Delay(wait < BusyPollInterval ? wait : BusyPollInterval, token).AnyContext();
                }
            }
        }
        catch (OperationCanceledException) when (_disposing.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return [];
        }
        finally
        {
            _receiveLock.Release();
        }
    }

    public async Task CompleteAsync(IncomingMessage message, CancellationToken cancellationToken = default)
    {
        var (delivery, session) = GetDelivery(message);
        try
        {
            await session.Receiver.CompleteMessageAsync(delivery.Received, cancellationToken).AnyContext();
        }
        catch (ServiceBusException ex) when (AzureServiceBusMessageTransport.IsLockLost(ex))
        {
            throw LeaseLost(delivery, session, ex);
        }
        catch (ObjectDisposedException ex) when (!IsDisposed)
        {
            throw LeaseLost(delivery, session, ex);
        }
        catch (ServiceBusException ex)
        {
            throw new MessageBusException($"Could not complete message {delivery.MessageId} from {Source}: {ex.Message}", ex);
        }

        session.Settle(delivery.Received);
    }

    /// <remarks>
    /// A retry with a delay renews the session lock, which proves this delivery still holds it, and keeps the message
    /// locked until the delay has passed; then it is abandoned and received again before the rest of its group.
    /// </remarks>
    public async Task AbandonAsync(IncomingMessage message, TimeSpan delay, CancellationToken cancellationToken = default)
    {
        var (delivery, session) = GetDelivery(message);
        ThrowIfLeaseExpired(delivery, session);
        if (delay > TimeSpan.Zero)
        {
            await RenewSessionAsync(session, delivery, cancellationToken).AnyContext();
            if (!AzureServiceBusMessageTransportCodec.IsUngrouped(delivery.Received))
            {
                session.AddRetry(delivery.Received, _transport.TimeProvider.GetUtcNow() + delay);
                return;
            }

            try
            {
                await _transport.ScheduleRetryAsync(session.Receiver, _entity, delivery, delay, Source, cancellationToken).AnyContext();
            }
            catch (ServiceBusException ex) when (AzureServiceBusMessageTransport.IsLockLost(ex))
            {
                throw LeaseLost(delivery, session, ex);
            }
            catch (ObjectDisposedException ex) when (!IsDisposed)
            {
                throw LeaseLost(delivery, session, ex);
            }
            catch (ServiceBusException ex)
            {
                throw new MessageBusException($"Could not schedule the retry of message {delivery.MessageId} from {Source}: {ex.Message}", ex);
            }

            session.Settle(delivery.Received);
            return;
        }

        try
        {
            await session.Receiver.AbandonMessageAsync(delivery.Received, cancellationToken: cancellationToken).AnyContext();
        }
        catch (ServiceBusException ex) when (AzureServiceBusMessageTransport.IsLockLost(ex))
        {
            throw LeaseLost(delivery, session, ex);
        }
        catch (ObjectDisposedException ex) when (!IsDisposed)
        {
            throw LeaseLost(delivery, session, ex);
        }
        catch (ServiceBusException ex)
        {
            throw new MessageBusException($"Could not abandon message {delivery.MessageId} from {Source}: {ex.Message}", ex);
        }

        session.Settle(delivery.Received);
    }

    public async Task DeadLetterAsync(IncomingMessage message, string reason, CancellationToken cancellationToken = default)
    {
        var (delivery, session) = GetDelivery(message);
        ThrowIfLeaseExpired(delivery, session);
        try
        {
            await session.Receiver.DeadLetterMessageAsync(delivery.Received,
                AzureServiceBusMessageTransportCodec.TruncateUtf8(reason ?? String.Empty, AzureServiceBusMessageTransportCodec.MaxDeadLetterReasonBytes),
                deadLetterErrorDescription: null, cancellationToken).AnyContext();
        }
        catch (ServiceBusException ex) when (AzureServiceBusMessageTransport.IsLockLost(ex))
        {
            throw LeaseLost(delivery, session, ex);
        }
        catch (ObjectDisposedException ex) when (!IsDisposed)
        {
            throw LeaseLost(delivery, session, ex);
        }
        catch (ServiceBusException ex)
        {
            throw new MessageBusException($"Could not dead-letter message {delivery.MessageId} from {Source}: {ex.Message}", ex);
        }

        session.Settle(delivery.Received);
    }

    /// <remarks>
    /// Renews the session lock, which every message of the session shares, so renewals of several of its messages
    /// share one request, and a renewal right after another returns the lease it got.
    /// </remarks>
    public async Task RenewAsync(IncomingMessage message, CancellationToken cancellationToken = default)
    {
        var (delivery, session) = GetDelivery(message);
        if (session.LeaseExpiresAt - _transport.TimeProvider.GetUtcNow() <= _lockDuration * 2 / 3)
            await RenewSessionAsync(session, delivery, cancellationToken).AnyContext();

        delivery.LeaseExpiresAt = session.LeaseExpiresAt;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        _transport.Untrack(this);
        await _disposing.CancelAsync().AnyContext();
        await _maintenance.AnyContext();

        bool locked = await _receiveLock.WaitAsync(ReleaseTimeout).AnyContext();
        try
        {
            List<AzureServiceBusSession> sessions;
            lock (_sessions)
            {
                sessions = [.. _sessions];
                _sessions.Clear();
            }

            await Task.WhenAll(sessions.Select(CloseAsync)).AnyContext();
        }
        finally
        {
            if (locked)
                _receiveLock.Release();
        }

        if (_entity.Instance)
            await _transport.Entities.TryDeleteInstanceSubscriptionAsync(_entity.Topic!, _entity.Subscription!).AnyContext();
    }

    /// <summary>
    /// Hands out held messages that are due and receives from sessions with nothing outstanding, until
    /// <paramref name="max"/> messages are collected. Sessions whose lock was lost or expired, or that have nothing
    /// more to receive, are closed.
    /// </summary>
    private async Task CollectAsync(List<IncomingMessage> messages, int max, CancellationToken cancellationToken)
    {
        List<AzureServiceBusSession> sessions;
        lock (_sessions)
            sessions = [.. _sessions];

        foreach (var session in sessions)
        {
            if (messages.Count >= max)
                return;

            // A session whose lock ran out is closed; what it handed out is abandoned first, if the lock still holds,
            // so it is delivered again with a higher delivery count, as when Service Bus lets a lock expire.
            var now = _transport.TimeProvider.GetUtcNow();
            if (session.IsLost || (session.LeaseExpiresAt <= now && !session.IsParked))
            {
                await RemoveAsync(session).AnyContext();
                continue;
            }

            foreach (var retry in session.TakeDueRetries(now))
                await ReleaseRetryAsync(session, retry).AnyContext();

            foreach (var held in session.TakeDueHeld(now, max - messages.Count, IsEarly))
                messages.Add(ToIncomingMessage(session, held));

            if (!session.CanReceive)
                continue;

            // Only a session with nothing handed out is renewed here: the lease of what it handed out must run out
            // unless Foundatio renews it.
            if (!session.IsNew && session.IsIdle && session.LeaseExpiresAt - now < _lockDuration / 2 && !await TryRenewAsync(session).AnyContext())
            {
                await RemoveAsync(session).AnyContext();
                continue;
            }

            IReadOnlyList<ServiceBusReceivedMessage> received;
            try
            {
                received = await session.Receiver.ReceiveMessagesAsync(max - messages.Count, session.IsNew ? NewSessionReceiveWait : HeldSessionReceiveWait, cancellationToken).AnyContext();
            }
            catch (Exception ex) when (ex is ObjectDisposedException || (ex is ServiceBusException serviceBusException
                && (AzureServiceBusMessageTransport.IsLockLost(serviceBusException) || serviceBusException.Reason == ServiceBusFailureReason.ServiceTimeout)))
            {
                _transport.Logger.LogDebug(ex, "Lost session {SessionId} of {Source}", session.SessionId, Source);
                session.MarkLost();
                await RemoveAsync(session).AnyContext();
                continue;
            }
            catch (ServiceBusException ex) when (AzureServiceBusMessageTransport.IsFatal(ex))
            {
                throw new MessageReceiverFaultedException($"{_entity} of {Source} can no longer be received from: {ex.Message}", ex);
            }
            catch (ServiceBusException ex)
            {
                throw new MessageBusException($"Could not receive from session {session.SessionId} of {Source}: {ex.Message}", ex);
            }

            session.IsNew = false;
            if (received.Count == 0)
            {
                if (session.IsIdle)
                    await RemoveAsync(session).AnyContext();

                continue;
            }

            foreach (var message in session.Add(received, _transport.TimeProvider.GetUtcNow(), IsEarly))
                messages.Add(ToIncomingMessage(session, message));
        }
    }

    /// <summary>Accepts the next session that has messages, or returns null when none does within <paramref name="wait"/>.</summary>
    private async Task<AzureServiceBusSession?> AcceptAsync(TimeSpan wait, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(wait);
        var requestedAt = _transport.TimeProvider.GetUtcNow();
        ServiceBusSessionReceiver receiver;
        try
        {
            receiver = _entity.Queue is not null
                ? await _transport.Client.AcceptNextSessionAsync(_entity.Queue, _sessionOptions, timeout.Token).AnyContext()
                : await _transport.Client.AcceptNextSessionAsync(_entity.Topic!, _entity.Subscription!, _sessionOptions, timeout.Token).AnyContext();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.ServiceTimeout)
        {
            return null;
        }
        catch (ServiceBusException ex) when (AzureServiceBusMessageTransport.IsFatal(ex))
        {
            throw new MessageReceiverFaultedException($"{_entity} of {Source} can no longer be received from: {ex.Message}", ex);
        }
        catch (ServiceBusException ex)
        {
            throw new MessageBusException($"Could not accept a session of {Source}: {ex.Message}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new MessageReceiverFaultedException($"Could not accept a session of {Source}: {ex.Message}", ex);
        }

        if (_entity.LockDuration is null && receiver.SessionLockedUntil - DateTimeOffset.UtcNow is var measured && measured > TimeSpan.Zero && measured <= AzureServiceBusMessageTransport.MaxLockDuration)
            _lockDuration = measured;

        return new AzureServiceBusSession(receiver, _transport.GetLeaseEnd(requestedAt, receiver.SessionLockedUntil, _entity.LockDuration));
    }

    /// <summary>Abandons retries that are due and renews the locks of parked sessions, which nothing else renews.</summary>
    private async Task MaintainAsync()
    {
        var token = _disposing.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(MaintenanceInterval, token).AnyContext();

                List<AzureServiceBusSession> sessions;
                lock (_sessions)
                    sessions = [.. _sessions];

                foreach (var session in sessions)
                {
                    if (session.IsLost)
                        continue;

                    foreach (var retry in session.TakeDueRetries(_transport.TimeProvider.GetUtcNow()))
                        await ReleaseRetryAsync(session, retry).AnyContext();

                    if (session.IsParked && session.LeaseExpiresAt - _transport.TimeProvider.GetUtcNow() < _lockDuration / 2)
                        await TryRenewAsync(session).AnyContext();
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _transport.Logger.LogWarning(ex, "Could not maintain the sessions held by the receiver for {Source}", Source);
            }
        }
    }

    private async Task ReleaseRetryAsync(AzureServiceBusSession session, AzureServiceBusSessionRetry retry)
    {
        try
        {
            using var timeout = new CancellationTokenSource(OperationTimeout);
            await session.Receiver.AbandonMessageAsync(retry.Message, cancellationToken: timeout.Token).AnyContext();
            session.CompleteRetry(retry, released: true);
        }
        catch (Exception ex) when (ex is ObjectDisposedException || (ex is ServiceBusException serviceBusException && AzureServiceBusMessageTransport.IsLockLost(serviceBusException)))
        {
            // The session lock is gone, so the session goes to the next receiver, which gets the message at once.
            session.MarkLost();
            session.CompleteRetry(retry, released: true);
        }
        catch (Exception ex)
        {
            _transport.Logger.LogWarning(ex, "Could not release the retry of a message of session {SessionId} of {Source}; trying again", session.SessionId, Source);
            session.CompleteRetry(retry, released: false);
        }
    }

    private async Task<bool> TryRenewAsync(AzureServiceBusSession session)
    {
        try
        {
            await session.RenewAsync(_transport.TimeProvider.GetUtcNow, LeaseEnd, OperationTimeout, CancellationToken.None).AnyContext();
            return true;
        }
        catch (Exception ex)
        {
            _transport.Logger.LogDebug(ex, "Could not renew the lock of session {SessionId} of {Source}", session.SessionId, Source);
            if (ex is ServiceBusException serviceBusException && AzureServiceBusMessageTransport.IsLockLost(serviceBusException))
                session.MarkLost();

            return false;
        }
    }

    private async Task RenewSessionAsync(AzureServiceBusSession session, AzureServiceBusIncomingMessage delivery, CancellationToken cancellationToken)
    {
        try
        {
            await session.RenewAsync(_transport.TimeProvider.GetUtcNow, LeaseEnd, OperationTimeout, cancellationToken).AnyContext();
        }
        catch (ServiceBusException ex) when (AzureServiceBusMessageTransport.IsLockLost(ex))
        {
            throw LeaseLost(delivery, session, ex);
        }
        catch (ObjectDisposedException ex) when (!IsDisposed)
        {
            throw LeaseLost(delivery, session, ex);
        }
        catch (ServiceBusException ex)
        {
            throw new MessageBusException($"Could not renew the lock of session {session.SessionId} of {Source}: {ex.Message}", ex);
        }
    }

    /// <summary>Closes a session, first abandoning what it handed out and its retries so their deliveries count.</summary>
    private async Task CloseAsync(AzureServiceBusSession session)
    {
        bool lost = session.IsLost;
        var release = session.TakeForRelease();
        using var timeout = new CancellationTokenSource(ReleaseTimeout);
        if (!lost)
        {
            foreach (var message in release)
            {
                try
                {
                    await session.Receiver.AbandonMessageAsync(message, cancellationToken: timeout.Token).AnyContext();
                }
                catch (Exception ex)
                {
                    _transport.Logger.LogDebug(ex, "Could not release a message of session {SessionId} of {Source}; it is delivered again when the session lock expires", session.SessionId, Source);
                    break;
                }
            }
        }

        try
        {
            await session.Receiver.CloseAsync(timeout.Token).AnyContext();
        }
        catch (Exception ex)
        {
            _transport.Logger.LogDebug(ex, "Could not close session {SessionId} of {Source}", session.SessionId, Source);
        }
    }

    private async Task RemoveAsync(AzureServiceBusSession session)
    {
        lock (_sessions)
            _sessions.Remove(session);

        await CloseAsync(session).AnyContext();
    }

    private IncomingMessage ToIncomingMessage(AzureServiceBusSession session, ServiceBusReceivedMessage message)
    {
        var decoded = AzureServiceBusMessageTransportCodec.Decode(message, deadLetter: false);
        return new AzureServiceBusIncomingMessage
        {
            Owner = this,
            Session = session,
            Received = message,
            MessageId = decoded.MessageId,
            Body = decoded.Body,
            Headers = decoded.Headers,
            GroupKey = decoded.GroupKey,
            DeliveryCount = decoded.DeliveryCount,
            EnqueuedAt = message.EnqueuedTime,
            LeaseExpiresAt = session.LeaseExpiresAt
        };
    }

    private DateTimeOffset? NextDueAt()
    {
        DateTimeOffset? next = null;
        lock (_sessions)
        {
            foreach (var session in _sessions)
            {
                if (session.NextDueAt is { } dueAt && (next is null || dueAt < next))
                    next = dueAt;
            }
        }

        return next;
    }

    private (int Handling, int Parked) CountSessions()
    {
        int handling = 0, parked = 0;
        lock (_sessions)
        {
            foreach (var session in _sessions)
            {
                if (session.IsParked)
                    parked++;
                else
                    handling++;
            }
        }

        return (handling, parked);
    }

    private DateTimeOffset LeaseEnd(DateTimeOffset requestedAt, DateTimeOffset lockedUntil) => _transport.GetLeaseEnd(requestedAt, lockedUntil, _entity.LockDuration);

    private static bool IsEarly(ServiceBusReceivedMessage message, DateTimeOffset now) =>
        AzureServiceBusMessageTransportCodec.GetDeliverAt(message) is { } deliverAt && deliverAt - now > EarlyTolerance;

    private (AzureServiceBusIncomingMessage Delivery, AzureServiceBusSession Session) GetDelivery(IncomingMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message is not AzureServiceBusIncomingMessage { Session: { } session } delivery || delivery.Owner != this)
            throw new ArgumentException("The message was not received by this receiver.", nameof(message));

        ThrowIfDisposed();
        if (session.IsLost || !session.IsHandedOut(delivery.Received))
            throw new MessageLeaseLostException($"The lock of session {session.SessionId} of {Source}, which message {delivery.MessageId} belongs to, was lost or the message was settled.");

        return (delivery, session);
    }

    private void ThrowIfLeaseExpired(AzureServiceBusIncomingMessage delivery, AzureServiceBusSession session)
    {
        if (_transport.TimeProvider.GetUtcNow() >= session.LeaseExpiresAt)
            throw LeaseLost(delivery, session, null);
    }

    private MessageLeaseLostException LeaseLost(AzureServiceBusIncomingMessage delivery, AzureServiceBusSession session, Exception? innerException)
    {
        session.MarkLost();
        session.Settle(delivery.Received);
        string message = $"The lock of session {session.SessionId} of {Source}, which message {delivery.MessageId} belongs to, was lost.";
        return innerException is null ? new MessageLeaseLostException(message) : new MessageLeaseLostException(message, innerException);
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) == 1 || _transport.IsDisposed;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);
}
