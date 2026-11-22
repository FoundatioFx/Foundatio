using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;

namespace Foundatio.Messaging.Transport;

/// <summary>
/// A process-local transport for development and tests. By default it supports every capability: unlimited delays,
/// ordered groups, deduplication and any message size. Pass <see cref="TransportCapabilities"/> to emulate the
/// guarantees and limits of a production transport instead. Share one instance between hosts to simulate several services.
/// </summary>
public sealed class InMemoryMessageTransport : IMessageTransport
{
    private static readonly TimeSpan DeduplicationWindow = TimeSpan.FromMinutes(5);

    private static readonly TransportCapabilities AllCapabilities = new()
    {
        MaxDelay = null,
        MaxSendBatchSize = 1000,
        MaxReceiveBatchSize = 1000,
        LeaseExpiration = true,
        OrderedGroups = true,
        Deduplication = true
    };

    private readonly ConcurrentDictionary<string, InMemorySource> _sources = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, InMemorySource>> _topics = new();
    private readonly Dictionary<string, DateTimeOffset> _deduplication = new(StringComparer.Ordinal);
    private readonly Queue<(string Key, DateTimeOffset ExpiresAt)> _deduplicationOrder = new();
    private readonly TimeProvider _timeProvider;
    private volatile Func<MessageSource, bool>? _receiveFilter;
    private TaskCompletionSource _receiveFilterChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

    /// <summary>Creates a transport that supports every capability, with no limits.</summary>
    public InMemoryMessageTransport(TimeProvider? timeProvider = null) : this(AllCapabilities, timeProvider)
    {
    }

    /// <summary>
    /// Creates a transport that offers only <paramref name="capabilities"/>, so tests observe what a production transport
    /// would do. Without <see cref="TransportCapabilities.OrderedGroups"/>, a group's messages are not held back for each
    /// other, so they may be handled concurrently and out of order. Without <see cref="TransportCapabilities.Deduplication"/>,
    /// deduplication ids are ignored. Without <see cref="TransportCapabilities.LeaseExpiration"/>, a delivery stays leased
    /// until it is settled or its receiver is disposed. Messages over <see cref="TransportCapabilities.MaxMessageSize"/>
    /// are rejected with <see cref="MessageTooLargeException"/>, and delays over <see cref="TransportCapabilities.MaxDelay"/>,
    /// <see cref="TransportCapabilities.MaxPublishDelay"/> or <see cref="TransportCapabilities.MaxRetryDelay"/> with
    /// <see cref="ArgumentOutOfRangeException"/>; Foundatio emulates longer send and retry delays as it does for the real
    /// transport. Leases and receive batches are cut to <see cref="TransportCapabilities.MaxLeaseDuration"/> and
    /// <see cref="TransportCapabilities.MaxReceiveBatchSize"/>. Receives on one receiver are always safe to call
    /// concurrently; <see cref="TransportCapabilities.MaxConcurrentReceives"/> decides how many Foundatio makes.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="capabilities"/> cannot be emulated: a batch size or concurrent receive count below 1, a negative delay limit, a
    /// <see cref="TransportCapabilities.MinRetryDelay"/> above a non-zero <see cref="TransportCapabilities.MaxRetryDelay"/>,
    /// or a message size or lease limit that is not positive.
    /// </exception>
    public InMemoryMessageTransport(TransportCapabilities capabilities, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ValidateCapabilities(capabilities);
        Capabilities = capabilities;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Every capability with no limits, or the capabilities this transport was created to emulate.</summary>
    public TransportCapabilities Capabilities { get; }

    /// <inheritdoc />
    public Task SendAsync(MessageDestination destination, IReadOnlyList<OutgoingMessage> messages, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(messages);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        var now = _timeProvider.GetUtcNow();
        ValidateOutgoing(destination, messages, now);
        var targets = GetTargets(destination);
        if (targets.Count == 0)
            return Task.CompletedTask;

        foreach (var message in messages)
        {
            if (Capabilities.Deduplication && message.DeduplicationId is { } deduplicationId && IsDuplicate($"{destination}|{deduplicationId}", now))
                continue;

            foreach (var target in targets)
                target.Add(message, now);
        }

        return Task.CompletedTask;
    }

    /// <summary>The queue, the one subscription, or the topic's subscriptions a send delivers to, looked up once per send.</summary>
    private ICollection<InMemorySource> GetTargets(MessageDestination destination)
    {
        switch (destination.Kind)
        {
            case MessageDestinationKind.Queue:
                return new[] { GetOrAddSource(MessageSource.Queue(destination.Name)) };
            case MessageDestinationKind.Subscription:
                var source = MessageSource.Subscription(destination.Name, destination.SubscriptionName!);
                var subscription = GetOrAddSource(source);
                _topics.GetOrAdd(source.Topic!, _ => new()).TryAdd(subscription.Key, subscription);
                return new[] { subscription };
            default:
                return _topics.TryGetValue(destination.Name, out var subscriptions) ? subscriptions.Values : Array.Empty<InMemorySource>();
        }
    }

    /// <inheritdoc />
    public Task<IMessageReceiver> CreateReceiverAsync(MessageSource source, ReceiverOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        ThrowIfDisposed();

        // Like a broker with a lease limit, grant at most the limit.
        if (Capabilities.MaxLeaseDuration is { } maxLease && options.LeaseDuration > maxLease)
            options = options with { LeaseDuration = maxLease };

        InMemorySource state;
        if (source.Kind == MessageSourceKind.InstanceSubscription)
        {
            state = CreateSource($"{source}/{Guid.NewGuid():N}", isDeadLetter: false);
            _sources[state.Key] = state;
            _topics.GetOrAdd(source.Topic!, _ => new()).TryAdd(state.Key, state);
        }
        else
        {
            state = GetOrAddSource(source);
            if (source.Kind == MessageSourceKind.Subscription)
                _topics.GetOrAdd(source.Topic!, _ => new()).TryAdd(state.Key, state);
        }

        return Task.FromResult<IMessageReceiver>(new Receiver(this, source, state, options));
    }

    /// <summary>
    /// Counts a queue's or durable subscription's messages exactly: queued, delayed (including retries waiting out
    /// their delay), in flight and dead-lettered. Returns null for an instance subscription, whose messages belong to
    /// one receiver.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="source"/> is a dead-letter source.</exception>
    public Task<MessageSourceStats?> GetStatsAsync(MessageSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ThrowIfDisposed();
        if (source.Kind == MessageSourceKind.DeadLetter)
            throw new ArgumentException($"{source} has no counts of its own; read DeadLettered from the stats of {source.DeadLetterOf}.", nameof(source));

        if (source.Kind == MessageSourceKind.InstanceSubscription)
            return Task.FromResult<MessageSourceStats?>(null);

        var now = _timeProvider.GetUtcNow();
        var (queued, delayed, inFlight) = _sources.TryGetValue(source.ToString(), out var state) ? state.CountByState(now) : (0, 0, 0);
        int deadLettered = _sources.TryGetValue(MessageSource.DeadLetter(source).ToString(), out var deadLetters) ? deadLetters.Count : 0;
        return Task.FromResult<MessageSourceStats?>(new MessageSourceStats
        {
            Source = source,
            Queued = queued,
            Delayed = delayed,
            InFlight = inFlight,
            DeadLettered = deadLettered
        });
    }

    /// <inheritdoc />
    public Task DeleteAsync(MessageSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ThrowIfDisposed();
        if (source.Kind == MessageSourceKind.InstanceSubscription)
            throw new ArgumentException("Instance subscriptions are removed by disposing their receiver.", nameof(source));

        if (source.Kind == MessageSourceKind.Subscription && _topics.TryGetValue(source.Topic!, out var subscriptions))
            subscriptions.TryRemove(source.ToString(), out _);

        RemoveSource(source.ToString());
        if (source.Kind != MessageSourceKind.DeadLetter)
            RemoveSource(MessageSource.DeadLetter(source).ToString());

        return Task.CompletedTask;
    }

    /// <summary>Waits until no queue or subscription holds a pending or in-flight message. Dead letters are ignored.</summary>
    public async Task<bool> WaitUntilIdleAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            while (true)
            {
                if (_sources.Values.Where(s => !s.IsDeadLetter).All(s => s.IsEmpty))
                    return true;

                await Task.Delay(10, timeoutSource.Token).AnyContext();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>
    /// Holds receives from the sources <paramref name="canReceive"/> rejects: they return nothing until it allows them,
    /// and messages stay where they are. Null receives from every source. Used by <c>FoundatioTestHarness</c> to pause
    /// handlers while keeping their receivers, and so their subscriptions, open.
    /// </summary>
    internal void SetReceiveFilter(Func<MessageSource, bool>? canReceive)
    {
        _receiveFilter = canReceive;
        Interlocked.Exchange(ref _receiveFilterChanged, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    }

    /// <summary>
    /// Counts a source's messages that are due now, delayed (including retries waiting out their delay) and in flight,
    /// including every receiver's copy of an instance subscription.
    /// </summary>
    internal (int Due, int Delayed, int InFlight) CountByState(MessageSource source)
    {
        var now = _timeProvider.GetUtcNow();
        string key = source.ToString();
        string instancePrefix = key + "/";
        int due = 0, delayed = 0, inFlight = 0;
        foreach (var (name, state) in _sources)
        {
            if (name != key && !(source.Kind == MessageSourceKind.InstanceSubscription && name.StartsWith(instancePrefix, StringComparison.Ordinal)))
                continue;

            var (queued, waiting, leased) = state.CountByState(now);
            due += queued;
            delayed += waiting;
            inFlight += leased;
        }

        return (due, delayed, inFlight);
    }

    /// <summary>The number of pending and in-flight messages in a source.</summary>
    public int GetMessageCount(MessageSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return _sources.TryGetValue(source.ToString(), out var state) ? state.Count : 0;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _disposed = true;
        foreach (var source in _sources.Values)
            source.Signal();

        return ValueTask.CompletedTask;
    }

    private InMemorySource GetOrAddSource(MessageSource source)
    {
        string key = source.ToString();
        return _sources.TryGetValue(key, out var existing) ? existing : _sources.GetOrAdd(key, static (k, state) => state.Transport.CreateSource(k, state.IsDeadLetter),
            (Transport: this, IsDeadLetter: source.Kind == MessageSourceKind.DeadLetter));
    }

    private InMemorySource CreateSource(string key, bool isDeadLetter) => new(key)
    {
        IsDeadLetter = isDeadLetter,
        OrderedGroups = Capabilities.OrderedGroups,
        LeaseExpiration = Capabilities.LeaseExpiration
    };

    private void ValidateOutgoing(MessageDestination destination, IReadOnlyList<OutgoingMessage> messages, DateTimeOffset now)
    {
        bool publish = destination.Kind == MessageDestinationKind.Topic;
        var maxDelay = publish ? Capabilities.MaxPublishDelay : Capabilities.MaxDelay;
        foreach (var message in messages)
        {
            ArgumentNullException.ThrowIfNull(message, nameof(messages));
            if (Capabilities.MaxMessageSize is { } maxSize && MessageSize.Of(message) is var size && size > maxSize)
            {
                throw new MessageTooLargeException($"Message {message.MessageId} is {size} bytes with its headers and ids, which exceeds this transport's limit of {maxSize} bytes.")
                {
                    Size = size,
                    Limit = maxSize
                };
            }

            if (maxDelay is { } limit && message.DeliverAt is { } deliverAt && deliverAt - now > limit)
                throw new ArgumentOutOfRangeException(nameof(messages), $"Message {message.MessageId} is delayed by {deliverAt - now}, longer than this transport's {(publish ? "MaxPublishDelay" : "MaxDelay")} of {limit}.");
        }
    }

    internal static void ValidateCapabilities(TransportCapabilities capabilities)
    {
        if (capabilities.MaxSendBatchSize < 1 || capabilities.MaxReceiveBatchSize < 1 || capabilities.MaxConcurrentReceives < 1)
            throw new ArgumentException("MaxSendBatchSize, MaxReceiveBatchSize and MaxConcurrentReceives must be at least 1.", nameof(capabilities));
        if (capabilities.MaxDelay < TimeSpan.Zero || capabilities.MaxPublishDelay < TimeSpan.Zero || capabilities.MaxRetryDelay < TimeSpan.Zero || capabilities.MinRetryDelay < TimeSpan.Zero)
            throw new ArgumentException("Delay limits cannot be negative.", nameof(capabilities));
        if (capabilities.MaxRetryDelay is { } maxRetryDelay && maxRetryDelay > TimeSpan.Zero && capabilities.MinRetryDelay > maxRetryDelay)
            throw new ArgumentException("MinRetryDelay cannot be longer than a non-zero MaxRetryDelay.", nameof(capabilities));
        if (capabilities.MaxMessageSize <= 0)
            throw new ArgumentException("MaxMessageSize must be positive, or null for no limit.", nameof(capabilities));
        if (capabilities.MaxLeaseDuration <= TimeSpan.Zero)
            throw new ArgumentException("MaxLeaseDuration must be positive, or null for no limit.", nameof(capabilities));
    }

    private void RemoveSource(string key)
    {
        if (_sources.TryRemove(key, out var state))
            state.Delete();
    }

    private bool IsDuplicate(string key, DateTimeOffset now)
    {
        lock (_deduplicationOrder)
        {
            // Ids are remembered in the order they were sent, so the expired ones are at the front.
            while (_deduplicationOrder.TryPeek(out var oldest) && oldest.ExpiresAt <= now)
            {
                _deduplicationOrder.Dequeue();
                if (_deduplication.TryGetValue(oldest.Key, out var expiresAt) && expiresAt == oldest.ExpiresAt)
                    _deduplication.Remove(oldest.Key);
            }

            if (_deduplication.TryGetValue(key, out var existing) && existing > now)
                return true;

            var expires = now.Add(DeduplicationWindow);
            _deduplication[key] = expires;
            _deduplicationOrder.Enqueue((key, expires));
            return false;
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private void RemoveInstanceSubscription(MessageSource source, InMemorySource state)
    {
        _sources.TryRemove(state.Key, out _);
        if (_topics.TryGetValue(source.Topic!, out var subscriptions))
            subscriptions.TryRemove(state.Key, out _);
    }

    private sealed class Receiver : IMessageReceiver
    {
        private readonly InMemoryMessageTransport _transport;
        private readonly InMemorySource _state;
        private readonly ReceiverOptions _options;
        private volatile bool _disposed;

        public Receiver(InMemoryMessageTransport transport, MessageSource source, InMemorySource state, ReceiverOptions options)
        {
            _transport = transport;
            Source = source;
            _state = state;
            _options = options;
        }

        public MessageSource Source { get; }

        public async Task<IReadOnlyList<IncomingMessage>> ReceiveAsync(int maxMessages, TimeSpan maxWait, CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxMessages, 1);
            maxMessages = Math.Min(maxMessages, _transport.Capabilities.MaxReceiveBatchSize);
            var deadline = _transport._timeProvider.GetUtcNow().Add(maxWait);

            while (true)
            {
                ObjectDisposedException.ThrowIf(_disposed || _transport._disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                if (_state.IsDeleted)
                    throw new MessageReceiverFaultedException($"{Source} was deleted while this receiver was open.");

                var now = _transport._timeProvider.GetUtcNow();
                if (_transport._receiveFilter is { } filter && !filter(Source))
                {
                    var filterChanged = Volatile.Read(ref _transport._receiveFilterChanged).Task;
                    if (_transport._receiveFilter is { } current && !current(Source))
                    {
                        if (deadline - now <= TimeSpan.Zero)
                            return [];

                        using var heldSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        await Task.WhenAny(filterChanged, Task.Delay(deadline - now, _transport._timeProvider, heldSource.Token)).AnyContext();
                        heldSource.Cancel();
                        continue;
                    }
                }

                var (messages, nextChange, changed) = _state.Take(this, maxMessages, now, _options.LeaseDuration);
                if (messages.Count > 0)
                    return messages;

                var remaining = deadline - now;
                if (remaining <= TimeSpan.Zero)
                    return [];

                var wait = nextChange is { } next && next - now < remaining ? next - now : remaining;
                using var delaySource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var delay = Task.Delay(wait < TimeSpan.Zero ? TimeSpan.Zero : wait, _transport._timeProvider, delaySource.Token);
                await Task.WhenAny(changed, delay).AnyContext();
                delaySource.Cancel();
            }
        }

        public Task CompleteAsync(IncomingMessage message, CancellationToken cancellationToken = default)
        {
            _state.Remove(GetDelivery(message));
            return Task.CompletedTask;
        }

        public Task AbandonAsync(IncomingMessage message, TimeSpan delay, CancellationToken cancellationToken = default)
        {
            var delivery = GetDelivery(message);
            if (delay > TimeSpan.Zero && _transport.Capabilities.MaxRetryDelay is { } maxDelay && delay > maxDelay)
                throw new ArgumentOutOfRangeException(nameof(delay), $"A retry delay of {delay} is longer than this transport's MaxRetryDelay of {maxDelay}.");

            _state.Release(delivery, _transport._timeProvider.GetUtcNow().Add(delay < TimeSpan.Zero ? TimeSpan.Zero : delay));
            return Task.CompletedTask;
        }

        public Task DeadLetterAsync(IncomingMessage message, string reason, CancellationToken cancellationToken = default)
        {
            var delivery = GetDelivery(message);
            var deadLetterSource = MessageSource.DeadLetter(Source.Kind == MessageSourceKind.DeadLetter ? Source.DeadLetterOf! : Source);
            _state.Remove(delivery);

            var headers = new Dictionary<string, string>(message.Headers) { [KnownHeaders.DeadLetterReason] = reason };
            var deadLetter = new OutgoingMessage { MessageId = message.MessageId, Body = message.Body, Headers = headers, GroupKey = message.GroupKey };
            _transport.GetOrAddSource(deadLetterSource).Add(deadLetter, _transport._timeProvider.GetUtcNow());

            return Task.CompletedTask;
        }

        public Task RenewAsync(IncomingMessage message, CancellationToken cancellationToken = default)
        {
            _state.Renew(GetDelivery(message), _transport._timeProvider.GetUtcNow(), _options.LeaseDuration);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (_disposed)
                return ValueTask.CompletedTask;

            _disposed = true;
            if (Source.Kind == MessageSourceKind.InstanceSubscription)
                _transport.RemoveInstanceSubscription(Source, _state);
            else
                _state.ReleaseAll(this);

            return ValueTask.CompletedTask;
        }

        private InMemoryDelivery GetDelivery(IncomingMessage message)
        {
            ArgumentNullException.ThrowIfNull(message);
            if (message is not InMemoryDelivery delivery || delivery.Owner != this)
                throw new ArgumentException("The message was not received by this receiver.", nameof(message));

            return delivery;
        }
    }

    private sealed class InMemoryDelivery : IncomingMessage
    {
        public required Receiver Owner { get; init; }
        public required InMemoryEntry Entry { get; init; }
        public required Guid Receipt { get; init; }
    }

    private sealed class InMemoryEntry
    {
        public required OutgoingMessage Message { get; init; }
        public required DateTimeOffset EnqueuedAt { get; init; }
        public DateTimeOffset AvailableAt { get; set; }
        public int DeliveryCount { get; set; }
        public Guid? Receipt { get; set; }
        public Receiver? Owner { get; set; }
        public DateTimeOffset LeaseExpiresAt { get; set; }
        public LinkedListNode<InMemoryEntry>? Node { get; set; }

        /// <summary>Where the entry was added in its source; entries are handed out in this order.</summary>
        public long Position { get; set; }

        /// <summary>The entry's place in its ordered group, or null when it is not ordered.</summary>
        public LinkedListNode<InMemoryEntry>? GroupNode { get; set; }

        /// <summary>The entry's place among the leases of its duration while it is indexed as leased, reused for every lease.</summary>
        public LinkedListNode<InMemoryEntry>? LeaseNode { get; set; }

        /// <summary>Which of its source's indexes the entry is in.</summary>
        public EntryIndex Index { get; set; }

        /// <summary>True once a receive has looked at the entry; until then it waits after the source's cursor.</summary>
        public bool Seen { get; set; }
    }

    private enum EntryIndex
    {
        None,
        Ready,
        Waiting,
        Leased
    }

    /// <summary>
    /// One queue or subscription. Besides every entry in the order added, it indexes the entries a receive can hand out
    /// now (by position), the ones it can hand out once they are due (by time), and the leased ones (in the order their
    /// leases end, one list per lease duration), so a receive takes the earliest available entries and stops once its
    /// batch is full instead of looking at every entry. Entries no receive has looked at yet wait after a cursor, in order. In an ordered group only the
    /// earliest entry is indexed as available; a later one goes out in the same batch right behind it, when it is due.
    /// </summary>
    private sealed class InMemorySource
    {
        private static readonly Comparer<InMemoryEntry> ByPosition = Comparer<InMemoryEntry>.Create(static (a, b) => a.Position.CompareTo(b.Position));
        private static readonly Comparer<InMemoryEntry> ByAvailableAt = Comparer<InMemoryEntry>.Create(static (a, b) => a.AvailableAt != b.AvailableAt ? a.AvailableAt.CompareTo(b.AvailableAt) : a.Position.CompareTo(b.Position));

        private readonly LinkedList<InMemoryEntry> _entries = new();
        private readonly SortedSet<InMemoryEntry> _ready = new(ByPosition);
        private readonly SortedSet<InMemoryEntry> _waiting = new(ByAvailableAt);
        private readonly List<(TimeSpan Duration, LinkedList<InMemoryEntry> Entries)> _leases = [];
        private readonly Dictionary<string, LinkedList<InMemoryEntry>> _groups = new(StringComparer.Ordinal);
        private LinkedListNode<InMemoryEntry>? _cursor;
        private long _nextPosition;
        private TaskCompletionSource _changed = NewSignal();
        private bool _changedObserved;
        private volatile bool _deleted;

        public InMemorySource(string key)
        {
            Key = key;
        }

        public string Key { get; }
        public bool IsDeadLetter { get; init; }
        public bool OrderedGroups { get; init; }
        public bool LeaseExpiration { get; init; }
        public bool IsDeleted => _deleted;

        public bool IsEmpty
        {
            get
            {
                lock (_entries)
                    return _entries.Count == 0;
            }
        }

        public int Count
        {
            get
            {
                lock (_entries)
                    return _entries.Count;
            }
        }

        /// <summary>Counts entries that are due, waiting for a future delivery time, and leased.</summary>
        public (int Queued, int Delayed, int InFlight) CountByState(DateTimeOffset now)
        {
            int queued = 0, delayed = 0, inFlight = 0;
            lock (_entries)
            {
                foreach (var entry in _entries)
                {
                    if (IsLeased(entry, now))
                        inFlight++;
                    else if (entry.AvailableAt > now)
                        delayed++;
                    else
                        queued++;
                }
            }

            return (queued, delayed, inFlight);
        }

        public void Add(OutgoingMessage message, DateTimeOffset now)
        {
            var entry = new InMemoryEntry
            {
                Message = message,
                EnqueuedAt = now,
                AvailableAt = message.DeliverAt is { } deliverAt && deliverAt > now ? deliverAt : now
            };

            lock (_entries)
            {
                entry.Position = ++_nextPosition;
                entry.Node = _entries.AddLast(entry);
                _cursor ??= entry.Node;

                // A group's messages are handed out in order: later ones wait while an earlier one is leased or not yet due.
                // Dead letters are not ordered, so all of a group's dead letters can be inspected at once.
                if (OrderedGroups && !IsDeadLetter && message.GroupKey is { } group)
                {
                    if (!_groups.TryGetValue(group, out var entries))
                        _groups[group] = entries = new LinkedList<InMemoryEntry>();

                    entry.GroupNode = entries.AddLast(entry);
                }
            }

            Signal();
        }

        public (IReadOnlyList<IncomingMessage> Messages, DateTimeOffset? NextChange, Task Changed) Take(Receiver owner, int maxMessages, DateTimeOffset now, TimeSpan leaseDuration)
        {
            lock (_entries)
            {
                var changed = _changed.Task;
                var result = new List<IncomingMessage>();

                // Without lease expiration a delivery stays leased until it is settled or its receiver is disposed, which signal.
                foreach (var (_, leases) in _leases)
                {
                    while (leases.First?.Value is { } ended && ended.LeaseExpiresAt <= now)
                    {
                        Unindex(ended);
                        if (IsHead(ended))
                            AddTo(_ready, ended, EntryIndex.Ready);
                    }
                }

                while (_waiting.Min is { } due && due.AvailableAt <= now)
                {
                    Unindex(due);
                    AddTo(_ready, due, EntryIndex.Ready);
                }

                List<InMemoryEntry>? followers = null;
                while (result.Count < maxMessages && NextAvailable(now) is { } entry)
                {
                    entry.DeliveryCount++;
                    entry.Receipt = Guid.NewGuid();
                    entry.Owner = owner;
                    Lease(entry, now, leaseDuration);

                    result.Add(new InMemoryDelivery
                    {
                        Owner = owner,
                        Entry = entry,
                        Receipt = entry.Receipt.Value,
                        MessageId = entry.Message.MessageId,
                        Body = entry.Message.Body,
                        Headers = entry.Message.Headers,
                        GroupKey = entry.Message.GroupKey,
                        EnqueuedAt = entry.EnqueuedAt,
                        DeliveryCount = entry.DeliveryCount
                    });

                    // Once a group's message is handed out, its next one may follow in the same batch.
                    if (entry.GroupNode?.Next?.Value is { Index: EntryIndex.None } next && IsAvailable(next, now))
                    {
                        next.Seen = true;
                        AddTo(_ready, next, EntryIndex.Ready);
                        (followers ??= []).Add(next);
                    }
                }

                // A follower that did not fit now waits behind the leased message before it.
                if (followers is not null)
                {
                    foreach (var follower in followers)
                    {
                        if (follower.Index == EntryIndex.Ready)
                            Unindex(follower);
                    }
                }

                AdvanceCursor();

                // A receive that got nothing waits for the next change; until one does, changes need not signal.
                if (result.Count == 0)
                    _changedObserved = true;

                DateTimeOffset? nextChange = _waiting.Min?.AvailableAt;
                foreach (var (_, leases) in _leases)
                {
                    if (leases.First?.Value is { } leased && (nextChange is null || leased.LeaseExpiresAt < nextChange))
                        nextChange = leased.LeaseExpiresAt;
                }

                return (result, nextChange, changed);
            }
        }

        public void Remove(InMemoryDelivery delivery)
        {
            lock (_entries)
            {
                ThrowIfStale(delivery);
                var entry = delivery.Entry;
                Unindex(entry);
                _entries.Remove(entry.Node!);
                entry.Node = null;
                if (entry.GroupNode is { } groupNode)
                {
                    var group = groupNode.List!;
                    var next = groupNode.Previous is null ? groupNode.Next?.Value : null;
                    group.Remove(groupNode);
                    entry.GroupNode = null;
                    if (group.Count == 0)
                        _groups.Remove(entry.Message.GroupKey!);
                    else if (next is not null)
                        TrackNewHead(next);
                }
            }

            Signal();
        }

        public void Release(InMemoryDelivery delivery, DateTimeOffset availableAt)
        {
            lock (_entries)
            {
                ThrowIfStale(delivery);
                var entry = delivery.Entry;
                Unindex(entry);
                entry.Receipt = null;
                entry.Owner = null;
                entry.AvailableAt = availableAt;
                if (IsHead(entry))
                    AddTo(_waiting, entry, EntryIndex.Waiting);
            }

            Signal();
        }

        public void Renew(InMemoryDelivery delivery, DateTimeOffset now, TimeSpan leaseDuration)
        {
            lock (_entries)
            {
                ThrowIfStale(delivery);
                var entry = delivery.Entry;
                Unindex(entry);
                Lease(entry, now, leaseDuration);
            }
        }

        public void Delete()
        {
            lock (_entries)
            {
                _deleted = true;
                _entries.Clear();
                _ready.Clear();
                _waiting.Clear();
                _leases.Clear();
                _groups.Clear();
                _cursor = null;
            }

            Signal();
        }

        public void ReleaseAll(Receiver owner)
        {
            lock (_entries)
            {
                foreach (var entry in _entries.Where(e => e.Owner == owner))
                {
                    Unindex(entry);
                    entry.Receipt = null;
                    entry.Owner = null;
                    if (IsHead(entry))
                        AddTo(_waiting, entry, EntryIndex.Waiting);
                }
            }

            Signal();
        }

        public void Signal()
        {
            TaskCompletionSource previous;
            lock (_entries)
            {
                if (!_changedObserved)
                    return;

                previous = _changed;
                _changed = NewSignal();
                _changedObserved = false;
            }

            previous.TrySetResult();
        }

        /// <summary>
        /// Takes the available entry earliest in the source out of the indexes, looking at entries after the cursor as it
        /// reaches them; null when none is available.
        /// </summary>
        private InMemoryEntry? NextAvailable(DateTimeOffset now)
        {
            while (true)
            {
                AdvanceCursor();
                var ready = _ready.Min;
                if (_cursor is null || ready is not null && ready.Position < _cursor.Value.Position)
                {
                    if (ready is not null)
                        Unindex(ready);

                    return ready;
                }

                var entry = _cursor.Value;
                entry.Seen = true;
                _cursor = _cursor.Next;
                if (!IsHead(entry))
                    continue;

                if (entry.AvailableAt > now)
                {
                    AddTo(_waiting, entry, EntryIndex.Waiting);
                    continue;
                }

                return entry;
            }
        }

        /// <summary>Moves the cursor past entries a receive has already looked at, so it never rests on one that may be removed.</summary>
        private void AdvanceCursor()
        {
            while (_cursor is { Value.Seen: true })
                _cursor = _cursor.Next;
        }

        /// <summary>Indexes the earliest entry of a group once the one before it is gone.</summary>
        private void TrackNewHead(InMemoryEntry entry)
        {
            // The cursor reaches entries no receive has looked at; a leased entry is indexed as leased, or stays leased
            // until it is settled when leases do not expire.
            if (!entry.Seen || entry.Index != EntryIndex.None || entry.Receipt is not null && !LeaseExpiration)
                return;

            AddTo(_waiting, entry, EntryIndex.Waiting);
        }

        /// <summary>
        /// Sets the entry's lease and, when leases expire, adds it to the end of the leases of its duration, which keeps
        /// each list in the order the leases end.
        /// </summary>
        private void Lease(InMemoryEntry entry, DateTimeOffset now, TimeSpan leaseDuration)
        {
            entry.LeaseExpiresAt = now.Add(leaseDuration);
            if (!LeaseExpiration)
                return;

            LinkedList<InMemoryEntry>? leases = null;
            foreach (var (duration, entries) in _leases)
            {
                if (duration == leaseDuration)
                {
                    leases = entries;
                    break;
                }
            }

            if (leases is null)
                _leases.Add((leaseDuration, leases = new LinkedList<InMemoryEntry>()));

            leases.AddLast(entry.LeaseNode ??= new LinkedListNode<InMemoryEntry>(entry));
            entry.Index = EntryIndex.Leased;
        }

        private bool IsLeased(InMemoryEntry entry, DateTimeOffset now) => entry.Receipt is not null && (!LeaseExpiration || entry.LeaseExpiresAt > now);

        private bool IsAvailable(InMemoryEntry entry, DateTimeOffset now) => !IsLeased(entry, now) && entry.AvailableAt <= now;

        private static bool IsHead(InMemoryEntry entry) => entry.GroupNode?.Previous is null;

        private static void AddTo(SortedSet<InMemoryEntry> index, InMemoryEntry entry, EntryIndex kind)
        {
            index.Add(entry);
            entry.Index = kind;
        }

        private void Unindex(InMemoryEntry entry)
        {
            switch (entry.Index)
            {
                case EntryIndex.Ready:
                    _ready.Remove(entry);
                    break;
                case EntryIndex.Waiting:
                    _waiting.Remove(entry);
                    break;
                case EntryIndex.Leased:
                    entry.LeaseNode!.List!.Remove(entry.LeaseNode);
                    break;
            }

            entry.Index = EntryIndex.None;
        }

        private void ThrowIfStale(InMemoryDelivery delivery)
        {
            if (delivery.Entry.Receipt != delivery.Receipt || delivery.Entry.Node?.List != _entries)
                throw new MessageLeaseLostException($"The lease for message {delivery.MessageId} was lost.");
        }

        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
