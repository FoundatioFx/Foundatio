using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Foundatio.AsyncEx;
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
/// Accepts and session receives run in the background, several at once, and outlive the <see cref="ReceiveAsync"/> that
/// started them, so a receive never waits on one session while another has messages, and an accept is never cancelled
/// after Service Bus may have locked a session for it. A receive returns as soon as anything can be handed out, and a
/// settlement wakes it, so a group whose earlier message was in flight is received from again at once.
/// </para>
/// <para>
/// A message sent with a delay and a group key is in its session from the start, carrying its delivery time; the receiver
/// that gets it holds it, and every later message of the session it received with it, until it is due. A retry with a
/// delay is held the same way and abandoned when it is due, so it is received again first. Held (parked) sessions are
/// renewed by a background loop and do not count toward the session limit. Beyond <see cref="MaxParkedSessions"/>, the
/// parked session due last is closed, so parked sessions never stop the receiver from accepting ready ones, and at the
/// limit sessions are accepted at a bounded rate. A retry of a message without a group key is a scheduled copy instead,
/// as without sessions, so it holds nothing back.
/// </para>
/// <para>
/// Closing a session, when it is closed early or the receiver is disposed, abandons what it handed out, which counts a
/// delivery, and its retries, which carry their due time so whoever receives them next holds them until then. Held
/// messages are released by closing the session, which does not count a delivery. A session nothing asks messages of
/// for a second is closed, so a receiver whose handlers are all busy does not keep sessions others could handle.
/// </para>
/// </remarks>
internal sealed class AzureServiceBusSessionReceiver : IMessageReceiver
{
    internal const int MaxParkedSessions = 256;

    /// <summary>
    /// The fewest messages asked of a session: a receive that comes back with fewer shows the session is drained, so it
    /// is closed once they are settled, without another receive to find it empty.
    /// </summary>
    private const int MinSessionReceive = 2;

    private static readonly TimeSpan NewSessionReceiveWait = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan HeldSessionReceiveWait = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan MinWait = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan EarlyTolerance = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ParkedAcceptInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan ParkedWarningInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan UnservedSessionTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(10);

    private readonly AzureServiceBusMessageTransport _transport;
    private readonly AzureServiceBusReceiverEntity _entity;
    private readonly ServiceBusSessionReceiverOptions _sessionOptions = new() { ReceiveMode = ServiceBusReceiveMode.PeekLock, PrefetchCount = 0 };
    private readonly List<AzureServiceBusSession> _sessions = [];
    private readonly HashSet<Task> _background = [];
    private readonly SemaphoreSlim _receiveLock = new(1, 1);
    private readonly AsyncAutoResetEvent _changed = new();
    private readonly CancellationTokenSource _disposing = new();
    private TimeSpan _lockDuration;
    private Task _maintenance = Task.CompletedTask;
    private Exception? _error;
    private DateTimeOffset _nextParkedAcceptAt;
    private DateTimeOffset _parkedWarnedAt = DateTimeOffset.MinValue;
    private long _lastDemandAt = Stopwatch.GetTimestamp();
    private int _demand;
    private int _accepting;
    private int _cursor;
    private bool _acceptsIdle = true;
    private bool _acceptDeferred;
    private bool _closing;
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
        Interlocked.Increment(ref _demand);
        try
        {
            await _receiveLock.WaitAsync(token).AnyContext();
            try
            {
                while (true)
                {
                    if (Volatile.Read(ref _error) is MessageReceiverFaultedException)
                        throw TakeError()!;

                    var now = _transport.TimeProvider.GetUtcNow();
                    RemoveFinishedSessions(now);
                    var sessions = SnapshotSessions(rotate: true);
                    var messages = HandOut(sessions, max, now);
                    if (messages.Count > 0)
                    {
                        if (TakeError() is { } skipped)
                            _transport.Logger.LogWarning(skipped, "Receiving from {Source} failed; handing out what its other sessions have", Source);

                        return messages;
                    }

                    if (TakeError() is { } error)
                        throw error;

                    // What is asked for now and arrives after this receive returns is handed out by the next one.
                    StartReceives(sessions, max, now);
                    var remaining = maxWait - Stopwatch.GetElapsedTime(started);
                    if (remaining <= TimeSpan.Zero)
                        return [];

                    await WaitForChangeAsync(GetWait(sessions, remaining, now), token).AnyContext();
                }
            }
            finally
            {
                _receiveLock.Release();
            }
        }
        catch (OperationCanceledException) when (_disposing.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return [];
        }
        finally
        {
            Volatile.Write(ref _lastDemandAt, Stopwatch.GetTimestamp());
            Interlocked.Decrement(ref _demand);
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

        Settle(session, delivery);
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
                _changed.Set();
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

            Settle(session, delivery);
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

        Settle(session, delivery, returned: true);
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

        Settle(session, delivery);
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
        lock (_sessions)
            _closing = true;

        await _disposing.CancelAsync().AnyContext();
        await _maintenance.AnyContext();
        await WaitForBackgroundAsync().AnyContext();

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
            await WaitForBackgroundAsync().AnyContext();
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
    /// Closes sessions whose lock was lost or ran out, and sessions with nothing more to receive. While more than
    /// <see cref="MaxParkedSessions"/> are parked, it closes the parked ones due last: Service Bus hands out sessions in
    /// no particular order, so a receiver that kept every parked session would stop accepting the ready ones.
    /// </summary>
    private void RemoveFinishedSessions(DateTimeOffset now)
    {
        List<AzureServiceBusSession>? finished = null;
        int closedParked = 0;
        lock (_sessions)
        {
            int parked = 0;
            for (int i = _sessions.Count - 1; i >= 0; i--)
            {
                var session = _sessions[i];
                bool isParked = session.IsParked;

                // A session whose lock ran out is closed; what it handed out is abandoned first, if the lock still holds,
                // so it is delivered again with a higher delivery count, as when Service Bus lets a lock expire.
                if (session.IsLost || (session.LeaseExpiresAt <= now && !isParked) || session.IsDone)
                {
                    (finished ??= []).Add(session);
                    _sessions.RemoveAt(i);
                }
                else if (isParked)
                {
                    parked++;
                }
            }

            if (parked > MaxParkedSessions)
            {
                var dueLast = _sessions
                    .Select((session, index) => (Session: session, Index: index, DueAt: session.IsParked ? session.NextDueAt : null))
                    .Where(s => s.DueAt is not null)
                    .OrderByDescending(s => s.DueAt)
                    .ThenByDescending(s => s.Index)
                    .Take(parked - MaxParkedSessions)
                    .Select(s => s.Session)
                    .ToList();

                foreach (var session in dueLast)
                    _sessions.Remove(session);

                (finished ??= []).AddRange(dueLast);
                closedParked = dueLast.Count;
            }
        }

        if (finished is null)
            return;

        foreach (var session in finished)
            Track(CloseAsync(session));

        if (closedParked > 0 && now - _parkedWarnedAt >= ParkedWarningInterval)
        {
            _parkedWarnedAt = now;
            _transport.Logger.LogWarning("The receiver for {Source} holds {MaxParkedSessions} parked sessions, whose first message is delayed or waits to be retried; " +
                "it closes the ones due last and accepts at most {AcceptRate} sessions a second while it is at the limit, so ready groups are found more slowly",
                Source, MaxParkedSessions, (int)(TimeSpan.FromSeconds(1) / ParkedAcceptInterval));
        }
    }

    /// <summary>Hands out held messages that are due, from each session in turn, until <paramref name="max"/>, and starts releasing retries that are due.</summary>
    private List<IncomingMessage> HandOut(List<AzureServiceBusSession> sessions, int max, DateTimeOffset now)
    {
        var messages = new List<IncomingMessage>();
        foreach (var session in sessions)
        {
            if (messages.Count >= max)
                break;

            foreach (var retry in session.TakeDueRetries(now))
                Track(ReleaseRetryAsync(session, retry));

            foreach (var held in session.TakeDueHeld(now, max - messages.Count, IsEarly))
                messages.Add(ToIncomingMessage(session, held));
        }

        return messages;
    }

    /// <summary>
    /// Asks for up to <paramref name="max"/> messages, counting on one from each receive and accept in flight: from the
    /// sessions that can be received from, sharing the count between them, then from new sessions. Accepts in flight
    /// never hold back a receive from a session the receiver has.
    /// </summary>
    private void StartReceives(List<AzureServiceBusSession> sessions, int max, DateTimeOffset now)
    {
        if (IsDisposed)
            return;

        int wanted = max - sessions.Count(s => s.IsReceiving);
        if (wanted <= 0)
            return;

        var receivable = sessions.Where(s => s.CanReceive(now)).Take(wanted).ToList();
        if (receivable.Count > 0)
        {
            int count = Math.Clamp((wanted + receivable.Count - 1) / receivable.Count, MinSessionReceive, AzureServiceBusMessageTransport.MaxBatchSize);
            foreach (var session in receivable)
            {
                session.BeginReceive(count);
                Track(ReceiveFromSessionAsync(session, count));
            }

            wanted -= receivable.Count;
        }

        wanted -= Volatile.Read(ref _accepting);
        if (wanted > 0)
            StartAccepts(wanted, now);
    }

    private void StartAccepts(int wanted, DateTimeOffset now)
    {
        int handling = 0, parked = 0;
        lock (_sessions)
        {
            if (_closing)
                return;

            foreach (var session in _sessions)
            {
                if (session.IsParked)
                    parked++;
                else
                    handling++;
            }
        }

        int accepting = Volatile.Read(ref _accepting);
        int count = Math.Min(wanted, _transport.Options.MaxConcurrentSessions - handling - accepting);

        // While accepts find no session, one waits for the next session at a time.
        if (Volatile.Read(ref _acceptsIdle))
            count = Math.Min(count, 1 - accepting);

        _acceptDeferred = false;
        if (count > 0 && parked >= MaxParkedSessions)
        {
            // At the limit, an accept likely brings another session due later than every parked one, which is closed again
            // at once, so accepts go one at a time at a bounded rate.
            if (accepting > 0 || now < _nextParkedAcceptAt)
            {
                _acceptDeferred = accepting == 0;
                return;
            }

            count = 1;
            _nextParkedAcceptAt = now + ParkedAcceptInterval;
        }

        for (int i = 0; i < count; i++)
        {
            Interlocked.Increment(ref _accepting);
            Track(AcceptAsync());
        }
    }

    /// <summary>How long to wait for a change: no longer than until a held message, a retry or a drained session is due, or the next accept may start.</summary>
    private TimeSpan GetWait(List<AzureServiceBusSession> sessions, TimeSpan remaining, DateTimeOffset now)
    {
        DateTimeOffset? next = _acceptDeferred ? _nextParkedAcceptAt : null;
        foreach (var session in sessions)
        {
            if (session.NextDueAt is { } dueAt && (next is null || dueAt < next))
                next = dueAt;
            if (session.NextPollAt is { } pollAt && (next is null || pollAt < next))
                next = pollAt;
        }

        if (next is not { } at || at - now >= remaining)
            return remaining;

        return at - now > MinWait ? at - now : MinWait;
    }

    private async Task WaitForChangeAsync(TimeSpan wait, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(wait);
        try
        {
            await _changed.WaitAsync(timeout.Token).AnyContext();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Accepts the next session that has messages. It is never cancelled but by disposing, since Service Bus may have
    /// locked a session for it by then; it ends when Service Bus has no session to give within its operation timeout.
    /// </summary>
    private async Task AcceptAsync()
    {
        try
        {
            ServiceBusSessionReceiver receiver;
            try
            {
                receiver = _entity.Queue is not null
                    ? await _transport.Client.AcceptNextSessionAsync(_entity.Queue, _sessionOptions, _disposing.Token).AnyContext()
                    : await _transport.Client.AcceptNextSessionAsync(_entity.Topic!, _entity.Subscription!, _sessionOptions, _disposing.Token).AnyContext();
            }
            catch (OperationCanceledException) when (_disposing.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException) when (IsDisposed)
            {
                return;
            }
            catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.ServiceTimeout)
            {
                Volatile.Write(ref _acceptsIdle, true);
                return;
            }
            catch (ServiceBusException ex) when (AzureServiceBusMessageTransport.IsFatal(ex))
            {
                SetError(_transport.EntityLost(_entity, Source, ex));
                return;
            }
            catch (ServiceBusException ex)
            {
                SetError(new MessageBusException($"Could not accept a session of {Source}: {ex.Message}", ex));
                return;
            }
            catch (UnauthorizedAccessException ex)
            {
                SetError(new MessageReceiverFaultedException($"Could not accept a session of {Source}: {ex.Message}", ex));
                return;
            }
            catch (Exception ex)
            {
                SetError(ex);
                return;
            }

            // The lock was taken while the accept waited, so it is measured from when the session arrived.
            var acceptedAt = _transport.TimeProvider.GetUtcNow();
            Volatile.Write(ref _acceptsIdle, false);
            if (_entity.LockDuration is null && receiver.SessionLockedUntil - DateTimeOffset.UtcNow is var measured && measured > TimeSpan.Zero && measured <= AzureServiceBusMessageTransport.MaxLockDuration)
                _lockDuration = measured;

            var session = new AzureServiceBusSession(receiver, LeaseEnd(acceptedAt, receiver.SessionLockedUntil));
            bool closing;
            lock (_sessions)
            {
                closing = _closing;
                if (!closing)
                    _sessions.Add(session);
            }

            if (closing)
                await CloseAsync(session).AnyContext();
        }
        finally
        {
            Interlocked.Decrement(ref _accepting);
            _changed.Set();
        }
    }

    private async Task ReceiveFromSessionAsync(AzureServiceBusSession session, int maxMessages)
    {
        try
        {
            var received = await session.Receiver.ReceiveMessagesAsync(maxMessages, session.IsNew ? NewSessionReceiveWait : HeldSessionReceiveWait, _disposing.Token).AnyContext();
            session.EndReceive(received, _transport.TimeProvider.GetUtcNow(), IsEarly);
        }
        catch (Exception ex)
        {
            session.EndReceive();
            OnReceiveFailed(session, ex);
        }
        finally
        {
            _changed.Set();
        }
    }

    private void OnReceiveFailed(AzureServiceBusSession session, Exception ex)
    {
        switch (ex)
        {
            case OperationCanceledException when _disposing.IsCancellationRequested:
                return;
            case ObjectDisposedException:
            case ServiceBusException serviceBusException when AzureServiceBusMessageTransport.IsLockLost(serviceBusException) || serviceBusException.Reason == ServiceBusFailureReason.ServiceTimeout:
                _transport.Logger.LogDebug(ex, "Lost session {SessionId} of {Source}", session.SessionId, Source);
                session.MarkLost();
                return;
            case ServiceBusException serviceBusException when AzureServiceBusMessageTransport.IsFatal(serviceBusException):
                SetError(_transport.EntityLost(_entity, Source, serviceBusException));
                return;
            case ServiceBusException:
                SetError(new MessageBusException($"Could not receive from session {session.SessionId} of {Source}: {ex.Message}", ex));
                return;
            case UnauthorizedAccessException:
                SetError(new MessageReceiverFaultedException($"Could not receive from session {session.SessionId} of {Source}: {ex.Message}", ex));
                return;
            default:
                SetError(ex);
                return;
        }
    }

    /// <summary>
    /// Releases retries that are due, renews the locks of sessions with nothing handed out, which Foundatio does not
    /// renew, and closes the sessions nothing has asked messages of for a while, so other receivers can take them.
    /// </summary>
    private async Task MaintainAsync()
    {
        var token = _disposing.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(MaintenanceInterval, token).AnyContext();
                Maintain(_transport.TimeProvider.GetUtcNow());
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

    private void Maintain(DateTimeOffset now)
    {
        bool unserved = IsUnserved();
        List<AzureServiceBusSession>? unservedSessions = null;
        foreach (var session in SnapshotSessions())
        {
            if (session.IsLost)
                continue;

            foreach (var retry in session.TakeDueRetries(now))
                Track(ReleaseRetryAsync(session, retry));

            if (unserved && session.IsUnserved)
                (unservedSessions ??= []).Add(session);
            else if (session.HasNothingHandedOut && !session.IsRenewing && session.LeaseExpiresAt - now < _lockDuration / 2)
                Track(TryRenewAsync(session));
        }

        if (unservedSessions is null)
            return;

        lock (_sessions)
        {
            // Checked again under the lock a receive takes to see the sessions, so none is closed under a receive.
            if (!IsUnserved())
                return;

            foreach (var session in unservedSessions)
                _sessions.Remove(session);
        }

        foreach (var session in unservedSessions)
            Track(CloseAsync(session));
    }

    private bool IsUnserved() =>
        Volatile.Read(ref _demand) == 0 && Stopwatch.GetElapsedTime(Volatile.Read(ref _lastDemandAt)) > UnservedSessionTimeout;

    private async Task ReleaseRetryAsync(AzureServiceBusSession session, AzureServiceBusSessionRetry retry)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_disposing.Token);
            timeout.CancelAfter(OperationTimeout);
            await session.Receiver.AbandonMessageAsync(retry.Message, cancellationToken: timeout.Token).AnyContext();
            session.CompleteRetry(retry, released: true);
        }
        catch (Exception ex) when (ex is ObjectDisposedException || (ex is ServiceBusException serviceBusException && AzureServiceBusMessageTransport.IsLockLost(serviceBusException)))
        {
            // The session lock is gone, so the session goes to the next receiver, which gets the message at once.
            session.MarkLost();
            session.CompleteRetry(retry, released: true);
        }
        catch (OperationCanceledException) when (_disposing.IsCancellationRequested)
        {
            session.CompleteRetry(retry, released: false);
        }
        catch (Exception ex)
        {
            _transport.Logger.LogWarning(ex, "Could not release the retry of a message of session {SessionId} of {Source}; trying again", session.SessionId, Source);
            session.CompleteRetry(retry, released: false);
        }
        finally
        {
            _changed.Set();
        }
    }

    private async Task TryRenewAsync(AzureServiceBusSession session)
    {
        try
        {
            await session.RenewAsync(_transport.TimeProvider.GetUtcNow, LeaseEnd, OperationTimeout, _disposing.Token).AnyContext();
        }
        catch (OperationCanceledException) when (_disposing.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _transport.Logger.LogDebug(ex, "Could not renew the lock of session {SessionId} of {Source}", session.SessionId, Source);
            if (ex is ServiceBusException serviceBusException && AzureServiceBusMessageTransport.IsLockLost(serviceBusException))
                session.MarkLost();

            _changed.Set();
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

    /// <summary>
    /// Closes a session, first abandoning what it handed out, so those deliveries count, and its retries, each with its
    /// due time, so the receiver that gets the session next holds the retry until then.
    /// </summary>
    private async Task CloseAsync(AzureServiceBusSession session)
    {
        bool lost = session.IsLost;
        var (handedOut, retries) = session.TakeForRelease();
        using var timeout = new CancellationTokenSource(ReleaseTimeout);
        if (!lost)
        {
            try
            {
                foreach (var message in handedOut)
                    await session.Receiver.AbandonMessageAsync(message, cancellationToken: timeout.Token).AnyContext();

                var now = _transport.TimeProvider.GetUtcNow();
                foreach (var retry in retries)
                {
                    var dueAt = retry.DueAt > now ? new Dictionary<string, object> { [AzureServiceBusMessageTransportCodec.DeliverAtProperty] = retry.DueAt.ToUnixTimeMilliseconds() } : null;
                    await session.Receiver.AbandonMessageAsync(retry.Message, dueAt, timeout.Token).AnyContext();
                }
            }
            catch (Exception ex)
            {
                _transport.Logger.LogDebug(ex, "Could not release a message of session {SessionId} of {Source}; it is delivered again when the session lock expires", session.SessionId, Source);
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

    private void Settle(AzureServiceBusSession session, AzureServiceBusIncomingMessage delivery, bool returned = false)
    {
        session.Settle(delivery.Received, returned);
        _changed.Set();
    }

    private void Track(Task task)
    {
        if (task.IsCompleted)
            return;

        lock (_background)
            _background.Add(task);

        task.ContinueWith(static (completed, state) =>
        {
            var background = (HashSet<Task>)state!;
            lock (background)
                background.Remove(completed);
        }, _background, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task WaitForBackgroundAsync()
    {
        while (true)
        {
            Task[] tasks;
            lock (_background)
                tasks = [.. _background];

            if (tasks.Length == 0)
                return;

            await Task.WhenAll(tasks).AnyContext();
        }
    }

    private void SetError(Exception error) => Volatile.Write(ref _error, error);

    private Exception? TakeError() => Interlocked.Exchange(ref _error, null);

    private List<AzureServiceBusSession> SnapshotSessions(bool rotate = false)
    {
        lock (_sessions)
        {
            if (!rotate || _sessions.Count < 2)
                return [.. _sessions];

            // Each receive starts at the next session, so a small receive does not always hand out of the same one.
            int start = (int)((uint)_cursor++ % (uint)_sessions.Count);
            var snapshot = new List<AzureServiceBusSession>(_sessions.Count);
            for (int i = 0; i < _sessions.Count; i++)
                snapshot.Add(_sessions[(start + i) % _sessions.Count]);

            return snapshot;
        }
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
        Settle(session, delivery);
        string message = $"The lock of session {session.SessionId} of {Source}, which message {delivery.MessageId} belongs to, was lost.";
        return innerException is null ? new MessageLeaseLostException(message) : new MessageLeaseLostException(message, innerException);
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) == 1 || _transport.IsDisposed;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);
}
