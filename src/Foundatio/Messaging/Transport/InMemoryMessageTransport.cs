using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;

namespace Foundatio.Messaging.Transport;

/// <summary>
/// A process-local transport for development and tests. It supports every capability: unlimited delays, ordered
/// groups, deduplication and any message size. Share one instance between hosts to simulate several services.
/// </summary>
public sealed class InMemoryMessageTransport : IMessageTransport
{
    private static readonly TimeSpan DeduplicationWindow = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, InMemorySource> _sources = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, InMemorySource>> _topics = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _deduplication = new();
    private readonly TimeProvider _timeProvider;
    private long _sequence;
    private bool _disposed;

    public InMemoryMessageTransport(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public TransportCapabilities Capabilities { get; } = new()
    {
        MaxDelay = null,
        MaxSendBatchSize = 1000,
        MaxReceiveBatchSize = 1000,
        LeaseExpiration = true,
        OrderedGroups = true,
        Deduplication = true
    };

    public Task SendAsync(MessageDestination destination, IReadOnlyList<OutgoingMessage> messages, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(messages);
        ThrowIfDisposed();

        var now = _timeProvider.GetUtcNow();
        foreach (var message in messages)
        {
            if (message.DeduplicationId is { } deduplicationId && IsDuplicate($"{destination}|{deduplicationId}", now))
                continue;

            if (destination.Kind == MessageDestinationKind.Queue)
            {
                GetOrAddSource(MessageSource.Queue(destination.Name)).Add(message, now, NextSequence());
                continue;
            }

            if (destination.Kind == MessageDestinationKind.Subscription)
            {
                var source = MessageSource.Subscription(destination.Name, destination.SubscriptionName!);
                var subscription = GetOrAddSource(source);
                _topics.GetOrAdd(source.Topic!, _ => new()).TryAdd(subscription.Key, subscription);
                subscription.Add(message, now, NextSequence());
                continue;
            }

            if (!_topics.TryGetValue(destination.Name, out var subscriptions))
                continue;

            foreach (var subscription in subscriptions.Values)
                subscription.Add(message, now, NextSequence());
        }

        return Task.CompletedTask;
    }

    public Task<IMessageReceiver> CreateReceiverAsync(MessageSource source, ReceiverOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        ThrowIfDisposed();

        InMemorySource state;
        if (source.Kind == MessageSourceKind.InstanceSubscription)
        {
            state = new InMemorySource($"{source}/{Guid.NewGuid():N}");
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

    public Task DeleteAsync(MessageSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ThrowIfDisposed();
        if (source.Kind == MessageSourceKind.InstanceSubscription)
            throw new ArgumentException("Instance subscriptions are removed by disposing their receiver.", nameof(source));

        _sources.TryRemove(source.ToString(), out _);
        if (source.Kind == MessageSourceKind.DeadLetter)
            return Task.CompletedTask;

        _sources.TryRemove(MessageSource.DeadLetter(source).ToString(), out _);
        if (source.Kind == MessageSourceKind.Subscription && _topics.TryGetValue(source.Topic!, out var subscriptions))
            subscriptions.TryRemove(source.ToString(), out _);

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

    /// <summary>The number of pending and in-flight messages in a source.</summary>
    public int GetMessageCount(MessageSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return _sources.TryGetValue(source.ToString(), out var state) ? state.Count : 0;
    }

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
        return _sources.GetOrAdd(key, k => new InMemorySource(k) { IsDeadLetter = source.Kind == MessageSourceKind.DeadLetter });
    }

    private bool IsDuplicate(string key, DateTimeOffset now)
    {
        foreach (var expired in _deduplication.Where(d => d.Value <= now))
            _deduplication.TryRemove(expired.Key, out _);

        return !_deduplication.TryAdd(key, now.Add(DeduplicationWindow));
    }

    private long NextSequence() => Interlocked.Increment(ref _sequence);

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
        private bool _disposed;

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
            var deadline = _transport._timeProvider.GetUtcNow().Add(maxWait);

            while (true)
            {
                ObjectDisposedException.ThrowIf(_disposed || _transport._disposed, this);
                cancellationToken.ThrowIfCancellationRequested();

                var now = _transport._timeProvider.GetUtcNow();
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
            _state.Release(GetDelivery(message), _transport._timeProvider.GetUtcNow().Add(delay < TimeSpan.Zero ? TimeSpan.Zero : delay));
            return Task.CompletedTask;
        }

        public Task DeadLetterAsync(IncomingMessage message, string reason, CancellationToken cancellationToken = default)
        {
            var delivery = GetDelivery(message);
            var deadLetterSource = MessageSource.DeadLetter(Source.Kind == MessageSourceKind.DeadLetter ? Source.DeadLetterOf! : Source);
            _state.Remove(delivery);

            var headers = new Dictionary<string, string>(message.Headers) { [KnownHeaders.DeadLetterReason] = reason };
            var deadLetter = new OutgoingMessage { MessageId = message.MessageId, Body = message.Body, Headers = headers, GroupKey = message.GroupKey };
            _transport.GetOrAddSource(deadLetterSource).Add(deadLetter, _transport._timeProvider.GetUtcNow(), _transport.NextSequence());

            return Task.CompletedTask;
        }

        public Task RenewAsync(IncomingMessage message, CancellationToken cancellationToken = default)
        {
            _state.Renew(GetDelivery(message), _transport._timeProvider.GetUtcNow().Add(_options.LeaseDuration));
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
        public required long Sequence { get; init; }
        public required DateTimeOffset EnqueuedAt { get; init; }
        public DateTimeOffset AvailableAt { get; set; }
        public int DeliveryCount { get; set; }
        public Guid? Receipt { get; set; }
        public Receiver? Owner { get; set; }
        public DateTimeOffset LeaseExpiresAt { get; set; }
        public LinkedListNode<InMemoryEntry>? Node { get; set; }
    }

    private sealed class InMemorySource
    {
        private readonly LinkedList<InMemoryEntry> _entries = new();
        private TaskCompletionSource _changed = NewSignal();

        public InMemorySource(string key)
        {
            Key = key;
        }

        public string Key { get; }
        public bool IsDeadLetter { get; init; }

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

        public void Add(OutgoingMessage message, DateTimeOffset now, long sequence)
        {
            var entry = new InMemoryEntry
            {
                Message = message,
                Sequence = sequence,
                EnqueuedAt = now,
                AvailableAt = message.DeliverAt is { } deliverAt && deliverAt > now ? deliverAt : now
            };

            lock (_entries)
                entry.Node = _entries.AddLast(entry);

            Signal();
        }

        public (IReadOnlyList<IncomingMessage> Messages, DateTimeOffset? NextChange, Task Changed) Take(Receiver owner, int maxMessages, DateTimeOffset now, TimeSpan leaseDuration)
        {
            lock (_entries)
            {
                var changed = _changed.Task;
                var result = new List<IncomingMessage>();
                var blockedGroups = new HashSet<string>(StringComparer.Ordinal);
                DateTimeOffset? nextChange = null;

                foreach (var entry in _entries)
                {
                    bool leased = entry.Receipt is not null && entry.LeaseExpiresAt > now;
                    bool available = !leased && entry.AvailableAt <= now;
                    if (!available)
                    {
                        var changesAt = leased ? entry.LeaseExpiresAt : entry.AvailableAt;
                        if (nextChange is null || changesAt < nextChange)
                            nextChange = changesAt;
                    }

                    // A group's messages are handed out in order: later ones wait while an earlier one is leased or not yet due.
                    // Dead letters are not ordered, so all of a group's dead letters can be inspected at once.
                    string? group = entry.Message.GroupKey;
                    string? orderedGroup = IsDeadLetter ? null : group;
                    if (orderedGroup is not null && blockedGroups.Contains(orderedGroup))
                        continue;

                    if (!available || result.Count >= maxMessages)
                    {
                        if (orderedGroup is not null)
                            blockedGroups.Add(orderedGroup);

                        continue;
                    }

                    entry.DeliveryCount++;
                    entry.Receipt = Guid.NewGuid();
                    entry.LeaseExpiresAt = now.Add(leaseDuration);
                    entry.Owner = owner;
                    result.Add(new InMemoryDelivery
                    {
                        Owner = owner,
                        Entry = entry,
                        Receipt = entry.Receipt.Value,
                        MessageId = entry.Message.MessageId,
                        Body = entry.Message.Body,
                        Headers = entry.Message.Headers,
                        GroupKey = group,
                        EnqueuedAt = entry.EnqueuedAt,
                        DeliveryCount = entry.DeliveryCount
                    });
                }

                return (result, nextChange, changed);
            }
        }

        public void Remove(InMemoryDelivery delivery)
        {
            lock (_entries)
            {
                ThrowIfStale(delivery);
                _entries.Remove(delivery.Entry.Node!);
                delivery.Entry.Node = null;
            }

            Signal();
        }

        public void Release(InMemoryDelivery delivery, DateTimeOffset availableAt)
        {
            lock (_entries)
            {
                ThrowIfStale(delivery);
                delivery.Entry.Receipt = null;
                delivery.Entry.Owner = null;
                delivery.Entry.AvailableAt = availableAt;
            }

            Signal();
        }

        public void Renew(InMemoryDelivery delivery, DateTimeOffset leaseExpiresAt)
        {
            lock (_entries)
            {
                ThrowIfStale(delivery);
                delivery.Entry.LeaseExpiresAt = leaseExpiresAt;
            }
        }

        public void ReleaseAll(Receiver owner)
        {
            lock (_entries)
            {
                foreach (var entry in _entries.Where(e => e.Owner == owner))
                {
                    entry.Receipt = null;
                    entry.Owner = null;
                }
            }

            Signal();
        }

        public void Signal()
        {
            TaskCompletionSource previous;
            lock (_entries)
            {
                previous = _changed;
                _changed = NewSignal();
            }

            previous.TrySetResult();
        }

        private void ThrowIfStale(InMemoryDelivery delivery)
        {
            if (delivery.Entry.Receipt != delivery.Receipt || delivery.Entry.Node?.List != _entries)
                throw new MessageLeaseLostException($"The lease for message {delivery.MessageId} was lost.");
        }

        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
