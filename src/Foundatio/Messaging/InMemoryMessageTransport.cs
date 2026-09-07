using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Foundatio.AsyncEx;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Messaging;

public sealed partial class InMemoryMessageTransport : IMessageTransport, ISupportsPull, ISupportsVisibilityTimeout, ISupportsDeadLetter, ISupportsRedeliveryDelay, ISupportsLockRenewal, ISupportsStats, ISupportsEphemeralSubscriptions, ITransportInfo
{
    private static readonly TimeSpan _defaultLockRenewal = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan _reclaimInterval = TimeSpan.FromMilliseconds(50);

    // Priority and expiration are honored on every role; there is no native delayed delivery (delays route through
    // the runtime-store fallback) and no broker-imposed size or batch limits.
    private static readonly TransportCapabilities _capabilities = new()
    {
        Priority = true,
        Expiration = true,
        Ordering = OrderingGuarantee.Fifo
    };

    private static readonly IReadOnlySet<DestinationRole> _supportedRoles = new HashSet<DestinationRole>
    {
        DestinationRole.Queue,
        DestinationRole.Topic,
        DestinationRole.Subscription,
        DestinationRole.Binding
    };

    private readonly ConcurrentDictionary<string, DestinationState> _destinations = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DestinationRole> _roles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _topicSubscriptions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<ITimer, string> _redeliveryTimers = new();
    private readonly ConcurrentDictionary<string, byte> _warnedDroppedTopics = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _disposeCancellationTokenSource = new();
    private readonly object _reclaimGate = new();
    private readonly ITimer _reclaimTimer;
    private int _reclaimActive;
    private int _reclaimRunning;
    private int _isDisposed;

    public InMemoryMessageTransport(TimeProvider? timeProvider = null, ILoggerFactory? loggerFactory = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = loggerFactory?.CreateLogger<InMemoryMessageTransport>() ?? NullLogger<InMemoryMessageTransport>.Instance;
        _reclaimTimer = _timeProvider.CreateTimer(ReclaimExpired, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public DeliveryGuarantee DeliveryGuarantee => DeliveryGuarantee.AtLeastOnce;
    public IReadOnlySet<DestinationRole> SupportedRoles => _supportedRoles;

    public TransportCapabilities GetCapabilities(DestinationAddress destination) => _capabilities;

    // The in-memory transport has no broker-imposed ceiling on visibility or redelivery delay.
    public TimeSpan? MaxVisibilityTimeout => null;
    public TimeSpan? MaxRedeliveryDelay => null;

    public Task<SendResult> SendAsync(DestinationAddress destination, IReadOnlyList<TransportMessage> messages, TransportSendOptions options, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(messages);

        if (options.DeliverAt is { } deliverAt && deliverAt > _timeProvider.GetUtcNow())
            throw new NotSupportedException($"Transport \"{GetType().Name}\" does not support native delayed delivery. Use the runtime-store scheduled dispatch fallback.");

        // The address role picks the physical namespace, so a queue and a topic can share a route name (a message
        // type that is both sent and published) without colliding or cross-delivering.
        string key = StorageKey(destination);

        var results = new SendItemResult[messages.Count];
        for (int index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            // Each message gets a unique id so per-message settlement never aliases across distinct messages.
            string messageId = Guid.NewGuid().ToString("N");
            var stored = CreateStoredMessage(key, messageId, message, options);
            EnqueueForDestination(key, stored);

            results[index] = new SendItemResult { MessageId = messageId };
        }

        return Task.FromResult(new SendResult { Items = results });
    }

    public Task<IReadOnlyList<TransportEntry>> ReceiveAsync(DestinationAddress source, ReceiveRequest request, CancellationToken ct)
    {
        return ReceiveAsync(source, request, visibility: TimeSpan.FromMinutes(1), ct);
    }

    public async Task<IReadOnlyList<TransportEntry>> ReceiveAsync(DestinationAddress source, ReceiveRequest request, TimeSpan visibility, CancellationToken ct)
    {
        return await ReceiveAsync(source, request, (TimeSpan?)visibility, ct).AnyContext();
    }

    private async Task<IReadOnlyList<TransportEntry>> ReceiveAsync(DestinationAddress source, ReceiveRequest request, TimeSpan? visibility, CancellationToken ct)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(source);

        int maxMessages = request.MaxMessages <= 0 ? 1 : request.MaxMessages;
        if (!_destinations.TryGetValue(ReceivableKey(source), out var state))
        {
            if (source.Role == DestinationRole.Subscription)
                throw new MessageDestinationNotFoundException(source, new InvalidOperationException("The subscription must be provisioned before receiving."));
            state = GetOrAddDestination(ReceivableKey(source));
        }
        var entries = new List<TransportEntry>(maxMessages);
        DateTimeOffset? waitUntil = request.MaxWaitTime is { } waitTime && waitTime > TimeSpan.Zero
            ? _timeProvider.GetUtcNow().Add(waitTime)
            : null;

        // Return any messages whose visibility window lapsed (consumer crashed without settling) to the queue so
        // they are redelivered with an incremented delivery count — honoring the advertised at-least-once contract.
        state.ReclaimExpired(_timeProvider.GetUtcNow());

        while (entries.Count < maxMessages)
        {
            if (TryReceive(source, state, visibility, out var entry))
            {
                entries.Add(entry);
                continue;
            }

            if (entries.Count > 0 || waitUntil is null)
                break;

            TimeSpan remaining = waitUntil.Value - _timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
                break;

            using var waitCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCancellationTokenSource.Token);
            waitCancellationTokenSource.CancelAfter(remaining);

            try
            {
                if (!await state.WaitToReadAsync(waitCancellationTokenSource.Token).ConfigureAwait(false))
                    break;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_disposeCancellationTokenSource.IsCancellationRequested)
            {
                break;
            }
        }

        return entries;
    }

    public Task CompleteAsync(TransportEntry entry, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(entry);

        var receipt = GetReceipt(entry);
        var state = GetExistingDestination(receipt.Destination);

        if (!state.InFlight.TryRemove(receipt.LockToken, out var inFlight) || !String.Equals(inFlight.Message.Id, entry.Id, StringComparison.Ordinal))
            throw new ReceiptExpiredException();

        Interlocked.Increment(ref state.Completed);
        return Task.CompletedTask;
    }

    public Task AbandonAsync(TransportEntry entry, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(entry);

        var receipt = GetReceipt(entry);
        var state = GetExistingDestination(receipt.Destination);

        if (!state.InFlight.TryRemove(receipt.LockToken, out var inFlight) || !String.Equals(inFlight.Message.Id, entry.Id, StringComparison.Ordinal))
            throw new ReceiptExpiredException();

        Interlocked.Increment(ref state.Abandoned);
        var redelivered = inFlight.Message with { DeliveryCount = entry.DeliveryCount + 1 };
        EnqueueStoredMessage(receipt.Destination, redelivered);

        return Task.CompletedTask;
    }

    public Task AbandonAsync(TransportEntry entry, TimeSpan redeliveryDelay, CancellationToken ct)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(entry);

        if (redeliveryDelay <= TimeSpan.Zero)
            return AbandonAsync(entry, ct);

        var receipt = GetReceipt(entry);
        var state = GetExistingDestination(receipt.Destination);

        if (!state.InFlight.TryRemove(receipt.LockToken, out var inFlight) || !String.Equals(inFlight.Message.Id, entry.Id, StringComparison.Ordinal))
            throw new ReceiptExpiredException();

        Interlocked.Increment(ref state.Abandoned);
        var redelivered = inFlight.Message with { DeliveryCount = entry.DeliveryCount + 1 };
        ScheduleRedelivery(receipt.Destination, redelivered, redeliveryDelay);

        return Task.CompletedTask;
    }

    public Task RenewLockAsync(TransportEntry entry, TimeSpan? duration, CancellationToken ct)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(entry);

        var receipt = GetReceipt(entry);
        var state = GetExistingDestination(receipt.Destination);

        if (!state.InFlight.TryGetValue(receipt.LockToken, out var inFlight) || !String.Equals(inFlight.Message.Id, entry.Id, StringComparison.Ordinal))
            throw new ReceiptExpiredException();

        // Renewal only extends a finite visibility window. A message received without a window holds an indefinite
        // lock, so there is nothing to extend — leave it as-is rather than imposing a window that could reclaim it.
        if (inFlight.VisibilityExpiresUtc is null)
            return Task.CompletedTask;

        var renewed = inFlight with { VisibilityExpiresUtc = _timeProvider.GetUtcNow().Add(duration ?? _defaultLockRenewal) };
        if (!state.InFlight.TryUpdate(receipt.LockToken, renewed, inFlight))
            throw new ReceiptExpiredException();

        return Task.CompletedTask;
    }

    public Task DeadLetterAsync(TransportEntry entry, string? reason, CancellationToken ct)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(entry);

        var receipt = GetReceipt(entry);
        var state = GetExistingDestination(receipt.Destination);

        if (!state.InFlight.TryRemove(receipt.LockToken, out var inFlight) || !String.Equals(inFlight.Message.Id, entry.Id, StringComparison.Ordinal))
            throw new ReceiptExpiredException();

        // Dead-letter with the caller's entry headers (which may carry forensics stamped by the core), not the
        // originally-stored ones.
        DeadLetter(state, inFlight.Message with { Headers = entry.Headers }, reason);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TransportEntry>> PeekDeadLetteredAsync(DestinationAddress destination, DeadLetterQuery? query = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(destination);
        query ??= new DeadLetterQuery();
        query.Validate();
        if (!_destinations.TryGetValue(ReceivableKey(destination), out var state))
            return Task.FromResult<IReadOnlyList<TransportEntry>>([]);
        var entries = state.Deadletters.Values.Where(m => query.AfterId is null || StringComparer.Ordinal.Compare(m.Id, query.AfterId) > 0)
            .OrderBy(m => m.Id, StringComparer.Ordinal).Take(query.Limit).Select(message => new TransportEntry
            {
                Id = message.Id,
                ApplicationMessageId = message.ApplicationMessageId,
                ContentType = message.ContentType,
                Destination = destination,
                Body = message.Body,
                Headers = message.Headers,
                DeliveryCount = message.DeliveryCount,
                EnqueuedUtc = message.EnqueuedUtc,
                Receipt = default
            }).ToArray();
        return Task.FromResult<IReadOnlyList<TransportEntry>>(entries);
    }

    public Task<bool> DeleteDeadLetteredAsync(DestinationAddress destination, string id, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (!_destinations.TryGetValue(ReceivableKey(destination), out var state))
            return Task.FromResult(false);
        lock (state.Deadletters)
            return Task.FromResult(state.Deadletters.TryRemove(id, out _));
    }

    public Task<bool> ReplayDeadLetteredAsync(DestinationAddress source, string id, DestinationAddress target, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (target.Role is not (DestinationRole.Queue or DestinationRole.Topic))
            throw new ArgumentException("Replay targets must be a queue or topic.", nameof(target));
        if (!_destinations.TryGetValue(ReceivableKey(source), out var state))
            return Task.FromResult(false);
        lock (state.Deadletters)
        {
            if (!state.Deadletters.TryGetValue(id, out var message))
                return Task.FromResult(false);
            var headers = MessageHeaders.Create(message.Headers.Where(h => !h.Key.Equals(KnownHeaders.Attempts, StringComparison.OrdinalIgnoreCase)
                && !h.Key.Equals(KnownHeaders.Expiration, StringComparison.OrdinalIgnoreCase)
                && !h.Key.StartsWith("message.dead_letter.", StringComparison.OrdinalIgnoreCase)));
            string key = StorageKey(target);
            var replayed = CreateStoredMessage(key, Guid.NewGuid().ToString("N"), new TransportMessage
            {
                MessageId = message.ApplicationMessageId,
                Body = message.Body,
                Headers = headers,
                ContentType = message.ContentType
            }, new TransportSendOptions());
            EnqueueForDestination(key, replayed);
            state.Deadletters.TryRemove(id, out _);
            return Task.FromResult(true);
        }
    }

    public Task<MessageDestinationStats> GetStatsAsync(DestinationAddress destination, CancellationToken ct)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(destination);

        if (!_destinations.TryGetValue(ReceivableKey(destination), out var state))
            return Task.FromResult(new MessageDestinationStats());

        return Task.FromResult(new MessageDestinationStats
        {
            Queued = state.QueuedCount,
            Working = state.InFlight.Count,
            Delayed = _redeliveryTimers.Values.LongCount(key => key == ReceivableKey(destination)),
            Deadletter = state.DeadletterCount,
            Enqueued = Volatile.Read(ref state.Enqueued),
            Dequeued = Volatile.Read(ref state.Dequeued),
            Completed = Volatile.Read(ref state.Completed),
            Abandoned = Volatile.Read(ref state.Abandoned)
        });
    }

    public Task EnsureAsync(IReadOnlyList<DestinationDeclaration> declarations, CancellationToken ct)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(declarations);

        foreach (var declaration in declarations)
        {
            var address = declaration.Address;
            ArgumentNullException.ThrowIfNull(address);

            lock (_temporarySubscriptions)
            {
                switch (address.Role)
                {
                    case DestinationRole.Queue:
                        GetOrAddDestination(StorageKey(address));
                        break;
                    case DestinationRole.Topic:
                        _roles.TryAdd(StorageKey(address), DestinationRole.Topic);
                        _topicSubscriptions.GetOrAdd(StorageKey(address), static _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));
                        break;
                    case DestinationRole.Subscription:
                        if (String.IsNullOrEmpty(address.Topic))
                            throw new ArgumentException("A subscription declaration must specify its owning topic.", nameof(declarations));

                        AddTopicSubscription(address.Topic, StorageKey(address));
                        break;
                    case DestinationRole.Binding:
                        if (String.IsNullOrEmpty(address.Topic))
                            throw new ArgumentException("A binding declaration must specify a source topic.", nameof(declarations));

                        AddTopicSubscription(address.Topic, StorageKey(address));
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(declarations), address.Role, "Unsupported destination role.");
                }
                if (declaration.AutoDeleteAfter is { } lease)
                    CreateTemporarySubscription(address, lease);
            }
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(DestinationAddress destination, CancellationToken ct)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(destination);

        lock (_temporarySubscriptions)
        {
            string key = StorageKey(destination);
            if (_temporarySubscriptions.Remove(key, out var lease))
                lease.Timer.Dispose();
            DeleteDestination(key);
        }

        return Task.CompletedTask;
    }

    private void DeleteDestination(string key)
    {
        _roles.TryRemove(key, out _);
        if (_destinations.TryRemove(key, out var removed))
            removed.Complete();
        _topicSubscriptions.TryRemove(key, out _);

        foreach (var subscriptions in _topicSubscriptions.Values)
            subscriptions.TryRemove(key, out _);

    }

    public Task<bool> ExistsAsync(DestinationAddress destination, CancellationToken ct)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(destination);

        return Task.FromResult(_roles.ContainsKey(StorageKey(destination)));
    }

    public ValueTask DisposeAsync()
    {
        DisposeTemporarySubscriptions();
        if (Interlocked.Exchange(ref _isDisposed, 1) == 1)
            return ValueTask.CompletedTask;

        _disposeCancellationTokenSource.Cancel();
        _disposeCancellationTokenSource.Dispose();
        lock (_reclaimGate)
            _reclaimTimer.Dispose();

        foreach (var timer in _redeliveryTimers.Keys)
        {
            if (_redeliveryTimers.TryRemove(timer, out _))
                timer.Dispose();
        }

        _destinations.Clear();
        _roles.Clear();
        _topicSubscriptions.Clear();
        return ValueTask.CompletedTask;
    }

    private void EnqueueForDestination(string key, StoredMessage message)
    {
        // Topic sends fan out one copy per subscription; a topic with no subscriptions drops the message (real
        // pub/sub semantics — subscriptions must exist before a publish can reach them). The drop is the single
        // most confusing beginner outcome ("I published and nothing happened"), so it is warned once per topic.
        if (key.StartsWith("t:", StringComparison.Ordinal))
        {
            if (!_topicSubscriptions.TryGetValue(key, out var subscriptions) || subscriptions.IsEmpty)
            {
                if (_warnedDroppedTopics.TryAdd(key, 0))
                    _logger.LogWarning("Message published to topic \"{Topic}\" was dropped: no subscriptions exist. Subscriptions are created when a handler subscribes (or via topology provisioning); publish after they exist. Further drops on this topic will not be logged", key[2..]);
                return;
            }

            foreach (string subscription in subscriptions.Keys)
                EnqueueStoredMessage(subscription, message with { Destination = subscription });

            return;
        }

        EnqueueStoredMessage(key, message);
    }

    private void EnqueueStoredMessage(string key, StoredMessage message)
    {
        var state = GetOrAddDestination(key);
        state.Enqueue(message with { Destination = key });
    }

    // Make an abandoned message invisible for the redelivery delay, then re-enqueue it. Re-enqueueing releases the
    // destination's availability semaphore, so a consumer blocked in a long receive wait wakes immediately when the
    // message becomes due. The one-shot timer is tracked so it can be disposed if the transport is torn down first.
    private void ScheduleRedelivery(string destination, StoredMessage message, TimeSpan delay)
    {
        ITimer? timer = null;
        timer = _timeProvider.CreateTimer(timerState =>
        {
            if (timer is not null && _redeliveryTimers.TryRemove(timer, out _))
                timer.Dispose();

            if (Volatile.Read(ref _isDisposed) == 1)
                return;

            try
            {
                EnqueueStoredMessage(destination, message);
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { } // destination was deleted / completed between scheduling and firing
        }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        _redeliveryTimers[timer] = destination;
        timer.Change(delay, Timeout.InfiniteTimeSpan);

        // A redelivery scheduled right as the transport disposes could otherwise leak its timer; clean up the race.
        if (Volatile.Read(ref _isDisposed) == 1 && _redeliveryTimers.TryRemove(timer, out _))
            timer.Dispose();
    }

    private void EnsureReclaimTimer()
    {
        if (Volatile.Read(ref _reclaimActive) == 1)
            return;
        lock (_reclaimGate)
        {
            if (_reclaimActive == 1 || Volatile.Read(ref _isDisposed) == 1)
                return;
            Volatile.Write(ref _reclaimActive, 1);
            _reclaimTimer.Change(_reclaimInterval, _reclaimInterval);
        }
    }

    private void ReclaimExpired(object? timerState)
    {
        if (Volatile.Read(ref _isDisposed) == 1 || Interlocked.CompareExchange(ref _reclaimRunning, 1, 0) != 0)
            return;
        try
        {
            var now = _timeProvider.GetUtcNow();
            foreach (var destination in _destinations)
            {
                try { destination.Value.ReclaimExpired(now); }
                catch (InvalidOperationException) { } // Destination was completed/deleted during reclamation.
            }

            if (HasInFlightMessages())
                return;
            lock (_reclaimGate)
            {
                if (Volatile.Read(ref _isDisposed) == 1)
                    return;
                // New receivers must observe the inactive flag before the final emptiness check.
                Volatile.Write(ref _reclaimActive, 0);
                if (HasInFlightMessages())
                    Volatile.Write(ref _reclaimActive, 1);
                else
                    _reclaimTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
        }
        finally
        {
            Volatile.Write(ref _reclaimRunning, 0);
        }
    }

    private bool HasInFlightMessages()
    {
        foreach (var destination in _destinations)
            if (!destination.Value.InFlight.IsEmpty)
                return true;
        return false;
    }

    private bool TryReceive(DestinationAddress source, DestinationState state, TimeSpan? visibility, out TransportEntry entry)
    {
        while (state.TryDequeue(out var message))
        {
            if (IsExpired(message))
            {
                DeadLetter(state, message, "expired");
                continue;
            }

            // The receipt carries the internal (role-qualified) key so settlement resolves the same state; the entry's
            // Destination stays the caller-facing source address.
            var receipt = new InMemoryReceipt(ReceivableKey(source), Guid.NewGuid().ToString("N"));
            DateTimeOffset? visibilityExpiresUtc = visibility is { } window ? _timeProvider.GetUtcNow().Add(window) : null;
            state.InFlight[receipt.LockToken] = new InFlightMessage(message, receipt, visibilityExpiresUtc);
            Interlocked.Increment(ref state.Dequeued);

            if (visibility is not null)
                EnsureReclaimTimer();

            entry = new TransportEntry
            {
                Id = message.Id,
                ApplicationMessageId = message.ApplicationMessageId,
                ContentType = message.ContentType,
                Destination = source,
                LockExpiresUtc = visibilityExpiresUtc,
                Body = message.Body,
                Headers = message.Headers,
                DeliveryCount = message.DeliveryCount,
                EnqueuedUtc = message.EnqueuedUtc,
                Receipt = new Receipt { TransportState = receipt }
            };
            return true;
        }

        entry = null!;
        return false;
    }

    private bool IsExpired(StoredMessage message)
    {
        string? expiration = message.Headers.GetValueOrDefault(KnownHeaders.Expiration);
        if (expiration is null)
            return false;

        return DateTimeOffset.TryParse(expiration, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expiresAt)
            && expiresAt <= _timeProvider.GetUtcNow();
    }

    private void DeadLetter(DestinationState state, StoredMessage message, string? reason)
    {
        if (!String.IsNullOrEmpty(reason))
            message = message with { Headers = message.Headers.ToBuilder().Set(KnownHeaders.DeadLetterReason, reason).Build() };

        state.Deadletter(message);
    }

    private StoredMessage CreateStoredMessage(string destination, string messageId, TransportMessage message, TransportSendOptions options)
    {
        var headers = message.Headers.ToBuilder()
            .SetIfMissing(KnownHeaders.Priority, options.Priority.ToString())
            .Build();
        int deliveryCount = Int32.TryParse(headers.GetValueOrDefault(KnownHeaders.Attempts), NumberStyles.Integer, CultureInfo.InvariantCulture, out int attempts) && attempts > 0
            ? attempts
            : 1;

        return new StoredMessage(
            messageId,
            message.MessageId,
            message.ContentType,
            destination,
            message.Body.ToArray(),
            headers,
            NormalizePriority(options.Priority),
            DeliveryCount: deliveryCount,
            EnqueuedUtc: _timeProvider.GetUtcNow());
    }

    // Internal state is keyed by role-qualified names derived from the canonical address: "t:" for topics, "q:" for
    // every receivable destination (queues AND subscriptions — a subscription is a queue-shaped destination a topic
    // fans into, exactly like an SNS-bound SQS queue, keyed by its topic-qualified address key). This gives a
    // queue/subscription and a topic sharing a route name distinct namespaces, as real brokers do.
    private static string StorageKey(DestinationAddress address) =>
        address.Role == DestinationRole.Topic ? "t:" + address.Name : "q:" + address.Key;

    // Receive-path keys are always queue-shaped; receive/stats/dead-letter reads never target a topic.
    private static string ReceivableKey(DestinationAddress address) => "q:" + address.Key;

    private static DestinationRole RoleForKey(string key) => key[0] == 't' ? DestinationRole.Topic : DestinationRole.Queue;

    private DestinationState GetOrAddDestination(string key)
    {
        _roles.TryAdd(key, RoleForKey(key));
        return _destinations.GetOrAdd(key, static _ => new DestinationState());
    }

    private DestinationState GetExistingDestination(string key)
    {
        if (_destinations.TryGetValue(key, out var destination))
            return destination;

        throw new ReceiptExpiredException($"The destination \"{key}\" no longer exists.");
    }

    private void AddTopicSubscription(string topic, string subscriptionStorageKey)
    {
        string topicKey = "t:" + topic;
        _roles.TryAdd(topicKey, DestinationRole.Topic);
        GetOrAddDestination(subscriptionStorageKey);
        var subscriptions = _topicSubscriptions.GetOrAdd(topicKey, static _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));
        subscriptions[subscriptionStorageKey] = 0;
    }

    private static MessagePriority NormalizePriority(MessagePriority priority)
    {
        return priority switch
        {
            MessagePriority.Low => MessagePriority.Low,
            MessagePriority.Normal => MessagePriority.Normal,
            MessagePriority.High => MessagePriority.High,
            _ => MessagePriority.Normal
        };
    }

    private static InMemoryReceipt GetReceipt(TransportEntry entry)
    {
        return entry.Receipt.TransportState as InMemoryReceipt ?? throw new ReceiptExpiredException();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) == 1, this);
    }

    private sealed record StoredMessage(
        string Id,
        string? ApplicationMessageId,
        string? ContentType,
        string Destination,
        ReadOnlyMemory<byte> Body,
        MessageHeaders Headers,
        MessagePriority Priority,
        int DeliveryCount,
        DateTimeOffset EnqueuedUtc);

    private sealed record InFlightMessage(StoredMessage Message, InMemoryReceipt Receipt, DateTimeOffset? VisibilityExpiresUtc);

    private sealed record InMemoryReceipt(string Destination, string LockToken);

    private sealed class DestinationState
    {
        private readonly Channel<StoredMessage>[] _channels =
        [
            Channel.CreateUnbounded<StoredMessage>(CreateChannelOptions()),
            Channel.CreateUnbounded<StoredMessage>(CreateChannelOptions()),
            Channel.CreateUnbounded<StoredMessage>(CreateChannelOptions())
        ];

        public ConcurrentDictionary<string, StoredMessage> Deadletters { get; } = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim _availableMessages = new(0);
        private long _queuedCount;
        private int _isCompleted;

        public ConcurrentDictionary<string, InFlightMessage> InFlight { get; } = new(StringComparer.Ordinal);
        public long Enqueued;
        public long Dequeued;
        public long Completed;
        public long Abandoned;
        public long Deadlettered;

        public long QueuedCount => Volatile.Read(ref _queuedCount);
        public long DeadletterCount => Deadletters.Count;

        public void Enqueue(StoredMessage message)
        {
            if (!_channels[(int)message.Priority].Writer.TryWrite(message))
                throw new InvalidOperationException("The destination is no longer accepting messages.");

            Interlocked.Increment(ref _queuedCount);
            Interlocked.Increment(ref Enqueued);
            _availableMessages.Release();
        }

        public bool TryDequeue(out StoredMessage message)
        {
            for (int index = (int)MessagePriority.High; index >= (int)MessagePriority.Low; index--)
            {
                if (_channels[index].Reader.TryRead(out message!))
                {
                    _availableMessages.Wait(0);
                    Interlocked.Decrement(ref _queuedCount);
                    return true;
                }
            }

            message = null!;
            return false;
        }

        public async ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken)
        {
            if (QueuedCount > 0)
                return true;

            await _availableMessages.WaitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        public void Deadletter(StoredMessage message)
        {
            if (Volatile.Read(ref _isCompleted) == 1)
                throw new InvalidOperationException("The destination is no longer accepting dead-letter messages.");
            Deadletters[message.Id] = message;
            Interlocked.Increment(ref Deadlettered);
        }

        public void ReclaimExpired(DateTimeOffset now)
        {
            if (Volatile.Read(ref _isCompleted) == 1)
                return;

            foreach (var kvp in InFlight)
            {
                if (kvp.Value.VisibilityExpiresUtc is { } expiry && expiry <= now && InFlight.TryRemove(kvp))
                {
                    Interlocked.Increment(ref Abandoned);
                    Enqueue(kvp.Value.Message with { DeliveryCount = kvp.Value.Message.DeliveryCount + 1 });
                }
            }
        }

        public void Complete()
        {
            if (Interlocked.Exchange(ref _isCompleted, 1) == 1)
                return;

            foreach (var channel in _channels)
                channel.Writer.TryComplete();

            Deadletters.Clear();
        }

        private static UnboundedChannelOptions CreateChannelOptions()
        {
            return new UnboundedChannelOptions
            {
                AllowSynchronousContinuations = false,
                SingleReader = false,
                SingleWriter = false
            };
        }
    }

}
