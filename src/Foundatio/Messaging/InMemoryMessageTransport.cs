using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;
using Foundatio.AsyncEx;
using Foundatio.Queues;
using Foundatio.Utility;

namespace Foundatio.Messaging;

public sealed class InMemoryMessageTransport : IMessageTransport, ISupportsPull, ISupportsPush, ISupportsRedeliveryDelay, ISupportsDeadLetter, ISupportsStats, ISupportsPriority, ISupportsDelayedDelivery, ISupportsExpiration, ISupportsProvisioning, ITransportInfo
{
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
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _disposeCancellationTokenSource = new();
    private int _isDisposed;

    public InMemoryMessageTransport(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public DeliveryGuarantee DeliveryGuarantee => DeliveryGuarantee.AtLeastOnce;
    public OrderingGuarantee Ordering => OrderingGuarantee.Fifo;
    public IReadOnlySet<DestinationRole> SupportedRoles => _supportedRoles;
    public int? MaxBatchSize => null;
    public long? MaxMessageBytes => null;

    public Task<SendResult> SendAsync(string destination, IReadOnlyList<TransportMessage> messages, TransportSendOptions options, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrEmpty(destination);
        ArgumentNullException.ThrowIfNull(messages);

        var results = new SendItemResult[messages.Count];
        for (int index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            string messageId = message.MessageId ?? options.DeduplicationId ?? Guid.NewGuid().ToString("N");
            var stored = CreateStoredMessage(destination, messageId, message, options);
            EnqueueOrSchedule(destination, stored, options);

            results[index] = new SendItemResult
            {
                MessageId = messageId,
                Success = true
            };
        }

        return Task.FromResult(new SendResult { Items = results });
    }

    public Task<IReadOnlyList<TransportEntry>> ReceiveAsync(string source, ReceiveRequest request, CancellationToken ct)
    {
        return ReceiveAsync(source, request, visibility: null, ct);
    }

    public async Task<IReadOnlyList<TransportEntry>> ReceiveAsync(string source, ReceiveRequest request, TimeSpan visibility, CancellationToken ct)
    {
        return await ReceiveAsync(source, request, (TimeSpan?)visibility, ct).AnyContext();
    }

    private async Task<IReadOnlyList<TransportEntry>> ReceiveAsync(string source, ReceiveRequest request, TimeSpan? visibility, CancellationToken ct)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrEmpty(source);

        int maxMessages = request.MaxMessages <= 0 ? 1 : request.MaxMessages;
        var state = GetOrAddDestination(source, DestinationRole.Queue);
        var entries = new List<TransportEntry>(maxMessages);
        DateTimeOffset? waitUntil = request.MaxWaitTime is { } waitTime && waitTime > TimeSpan.Zero
            ? _timeProvider.GetUtcNow().Add(waitTime)
            : null;

        while (entries.Count < maxMessages)
        {
            if (TryReceive(source, state, out var entry))
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
        return AbandonAsync(entry, TimeSpan.Zero, ct);
    }

    public Task AbandonAsync(TransportEntry entry, TimeSpan redeliveryDelay, CancellationToken ct)
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

        if (redeliveryDelay > TimeSpan.Zero)
            _ = Run.DelayedAsync(redeliveryDelay, () => EnqueueStoredMessageAsync(receipt.Destination, redelivered), _timeProvider, _disposeCancellationTokenSource.Token);
        else
            EnqueueStoredMessage(receipt.Destination, redelivered);

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

        DeadLetter(state, inFlight.Message, reason);
        return Task.CompletedTask;
    }

    public Task<IPushSubscription> SubscribeAsync(string source, Func<TransportEntry, CancellationToken, Task> onMessage, PushOptions options, CancellationToken ct)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentNullException.ThrowIfNull(onMessage);
        ArgumentNullException.ThrowIfNull(options);

        var subscription = new PushSubscription(source);
        subscription.Start(RunPushSubscriptionAsync(source, onMessage, options, subscription.CancellationToken));
        return Task.FromResult<IPushSubscription>(subscription);
    }

    public Task<QueueStats> GetStatsAsync(string destination, CancellationToken ct)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrEmpty(destination);

        if (!_destinations.TryGetValue(destination, out var state))
            return Task.FromResult(new QueueStats());

        return Task.FromResult(new QueueStats
        {
            Queued = state.QueuedCount,
            Working = state.InFlight.Count,
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
            ArgumentException.ThrowIfNullOrEmpty(declaration.Name);

            switch (declaration.Role)
            {
                case DestinationRole.Queue:
                    GetOrAddDestination(declaration.Name, DestinationRole.Queue);
                    break;
                case DestinationRole.Topic:
                    SetRole(declaration.Name, DestinationRole.Topic);
                    _topicSubscriptions.GetOrAdd(declaration.Name, static _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));
                    break;
                case DestinationRole.Subscription:
                    GetOrAddDestination(declaration.Name, DestinationRole.Subscription);
                    if (!String.IsNullOrEmpty(declaration.Source))
                        AddTopicSubscription(declaration.Source, declaration.Name);
                    break;
                case DestinationRole.Binding:
                    if (String.IsNullOrEmpty(declaration.Source))
                        throw new ArgumentException("A binding declaration must specify a source topic.", nameof(declarations));

                    GetOrAddDestination(declaration.Name, DestinationRole.Subscription);
                    AddTopicSubscription(declaration.Source, declaration.Name);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(declarations), declaration.Role, "Unsupported destination role.");
            }
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(string name, CancellationToken ct)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrEmpty(name);

        _roles.TryRemove(name, out _);
        if (_destinations.TryRemove(name, out var removed))
            removed.Complete();
        _topicSubscriptions.TryRemove(name, out _);

        foreach (var subscriptions in _topicSubscriptions.Values)
            subscriptions.TryRemove(name, out _);

        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string name, CancellationToken ct)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrEmpty(name);

        return Task.FromResult(_roles.ContainsKey(name));
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) == 1)
            return ValueTask.CompletedTask;

        _disposeCancellationTokenSource.Cancel();
        _disposeCancellationTokenSource.Dispose();
        _destinations.Clear();
        _roles.Clear();
        _topicSubscriptions.Clear();
        return ValueTask.CompletedTask;
    }

    private async Task RunPushSubscriptionAsync(string source, Func<TransportEntry, CancellationToken, Task> onMessage, PushOptions options, CancellationToken subscriptionCancellationToken)
    {
        using var linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(subscriptionCancellationToken, _disposeCancellationTokenSource.Token);
        var token = linkedCancellationTokenSource.Token;
        int maxMessages = Math.Max(1, options.MaxConcurrentMessages);

        while (!token.IsCancellationRequested)
        {
            IReadOnlyList<TransportEntry> entries;
            try
            {
                entries = await ReceiveAsync(source, new ReceiveRequest
                {
                    MaxMessages = maxMessages,
                    MaxWaitTime = options.PollInterval
                }, token).AnyContext();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }

            foreach (var entry in entries)
            {
                try
                {
                    await onMessage(entry, token).AnyContext();
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    await AbandonAsync(entry, token).AnyContext();
                }
            }
        }
    }

    private void EnqueueOrSchedule(string destination, StoredMessage message, TransportSendOptions options)
    {
        if (options.DeliverAt is { } deliverAt)
        {
            TimeSpan delay = deliverAt - _timeProvider.GetUtcNow();
            if (delay > TimeSpan.Zero)
            {
                _ = Run.DelayedAsync(delay, () => EnqueueForDestinationAsync(destination, message), _timeProvider, _disposeCancellationTokenSource.Token);
                return;
            }
        }

        EnqueueForDestination(destination, message);
    }

    private Task EnqueueForDestinationAsync(string destination, StoredMessage message)
    {
        EnqueueForDestination(destination, message);
        return Task.CompletedTask;
    }

    private void EnqueueForDestination(string destination, StoredMessage message)
    {
        var role = _roles.GetOrAdd(destination, DestinationRole.Queue);
        if (role == DestinationRole.Topic)
        {
            if (!_topicSubscriptions.TryGetValue(destination, out var subscriptions))
                return;

            foreach (string subscription in subscriptions.Keys)
                EnqueueStoredMessage(subscription, message with { Destination = subscription });

            return;
        }

        if (role is DestinationRole.Binding)
            throw new InvalidOperationException($"Cannot send directly to binding destination \"{destination}\".");

        EnqueueStoredMessage(destination, message);
    }

    private Task EnqueueStoredMessageAsync(string destination, StoredMessage message)
    {
        EnqueueStoredMessage(destination, message);
        return Task.CompletedTask;
    }

    private void EnqueueStoredMessage(string destination, StoredMessage message)
    {
        var role = _roles.GetOrAdd(destination, DestinationRole.Queue);
        var state = GetOrAddDestination(destination, role == DestinationRole.Topic ? DestinationRole.Queue : role);
        state.Enqueue(message with { Destination = destination });
    }

    private bool TryReceive(string source, DestinationState state, out TransportEntry entry)
    {
        while (state.TryDequeue(out var message))
        {
            if (IsExpired(message))
            {
                DeadLetter(state, message, "expired");
                continue;
            }

            var receipt = new InMemoryReceipt(source, Guid.NewGuid().ToString("N"));
            state.InFlight[receipt.LockToken] = new InFlightMessage(message, receipt);
            Interlocked.Increment(ref state.Dequeued);

            entry = new TransportEntry
            {
                Id = message.Id,
                Destination = source,
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

        return new StoredMessage(
            messageId,
            destination,
            message.Body.ToArray(),
            headers,
            NormalizePriority(options.Priority),
            DeliveryCount: 1,
            EnqueuedUtc: _timeProvider.GetUtcNow());
    }

    private DestinationState GetOrAddDestination(string name, DestinationRole role)
    {
        SetRole(name, role);
        return _destinations.GetOrAdd(name, static _ => new DestinationState());
    }

    private DestinationState GetExistingDestination(string name)
    {
        if (_destinations.TryGetValue(name, out var destination))
            return destination;

        throw new ReceiptExpiredException($"The destination \"{name}\" no longer exists.");
    }

    private void SetRole(string name, DestinationRole role)
    {
        _roles.AddOrUpdate(name, role, (_, existing) =>
        {
            if (existing == role)
                return existing;

            if (existing == DestinationRole.Queue && role == DestinationRole.Subscription)
                return role;

            if (existing == DestinationRole.Subscription && role == DestinationRole.Queue)
                return existing;

            throw new InvalidOperationException($"Destination \"{name}\" is already declared as {existing}.");
        });
    }

    private void AddTopicSubscription(string topic, string subscription)
    {
        SetRole(topic, DestinationRole.Topic);
        GetOrAddDestination(subscription, DestinationRole.Subscription);
        var subscriptions = _topicSubscriptions.GetOrAdd(topic, static _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));
        subscriptions[subscription] = 0;
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
        string Destination,
        ReadOnlyMemory<byte> Body,
        MessageHeaders Headers,
        MessagePriority Priority,
        int DeliveryCount,
        DateTimeOffset EnqueuedUtc);

    private sealed record InFlightMessage(StoredMessage Message, InMemoryReceipt Receipt);

    private sealed record InMemoryReceipt(string Destination, string LockToken);

    private sealed class DestinationState
    {
        private readonly Channel<StoredMessage>[] _channels =
        [
            Channel.CreateUnbounded<StoredMessage>(CreateChannelOptions()),
            Channel.CreateUnbounded<StoredMessage>(CreateChannelOptions()),
            Channel.CreateUnbounded<StoredMessage>(CreateChannelOptions())
        ];

        private readonly Channel<StoredMessage> _deadletterChannel = Channel.CreateUnbounded<StoredMessage>(CreateChannelOptions());
        private readonly SemaphoreSlim _availableMessages = new(0);
        private long _queuedCount;
        private long _deadletterCount;
        private int _isCompleted;

        public ConcurrentDictionary<string, InFlightMessage> InFlight { get; } = new(StringComparer.Ordinal);
        public long Enqueued;
        public long Dequeued;
        public long Completed;
        public long Abandoned;
        public long Deadlettered;

        public long QueuedCount => Volatile.Read(ref _queuedCount);
        public long DeadletterCount => Volatile.Read(ref _deadletterCount);

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
            if (!_deadletterChannel.Writer.TryWrite(message))
                throw new InvalidOperationException("The destination is no longer accepting dead-letter messages.");

            Interlocked.Increment(ref _deadletterCount);
            Interlocked.Increment(ref Deadlettered);
        }

        public void Complete()
        {
            if (Interlocked.Exchange(ref _isCompleted, 1) == 1)
                return;

            foreach (var channel in _channels)
                channel.Writer.TryComplete();

            _deadletterChannel.Writer.TryComplete();
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

    private sealed class PushSubscription : IPushSubscription
    {
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private Task? _worker;

        public PushSubscription(string source)
        {
            Source = source;
        }

        public string Source { get; }
        public CancellationToken CancellationToken => _cancellationTokenSource.Token;

        public void Start(Task worker)
        {
            _worker = worker;
        }

        public async ValueTask DisposeAsync()
        {
            await _cancellationTokenSource.CancelAsync().AnyContext();

            if (_worker is not null)
            {
                try
                {
                    await _worker.AnyContext();
                }
                catch (OperationCanceledException) { }
            }

            _cancellationTokenSource.Dispose();
        }
    }
}
