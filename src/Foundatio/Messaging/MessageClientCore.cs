using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Serializer;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;

namespace Foundatio.Messaging;

/// <summary>
/// Transport-neutral envelope options shared by queue send and pub/sub publish operations.
/// </summary>
internal sealed record MessageEnvelopeOptions
{
    public MessagePriority Priority { get; init; } = MessagePriority.Normal;
    public TimeSpan? Delay { get; init; }
    public DateTimeOffset? DeliverAt { get; init; }
    public TimeSpan? TimeToLive { get; init; }
    public string? CorrelationId { get; init; }
    public string? DeduplicationId { get; init; }
    public MessageHeaders? Headers { get; init; }
}

/// <summary>
/// Describes a consumer/subscription listener independent of whether it is backed by a queue or a pub/sub subscription.
/// </summary>
internal sealed record ListenerConfig
{
    public required string Source { get; init; }
    public required string Key { get; init; }
    public required Type MessageType { get; init; }
    public string Topic { get; init; } = "";
    public string Subscription { get; init; } = "";
    public AckMode AckMode { get; init; } = AckMode.Auto;
    public int MaxConcurrency { get; init; } = 1;
    public int MaxAttempts { get; init; } = 5;
    public Func<int, TimeSpan>? RedeliveryBackoff { get; init; }
}

/// <summary>
/// Shared implementation behind <see cref="MessageQueue"/> and <see cref="PubSub"/>: serialization, header/trace
/// construction, routing-agnostic send (with batch chunking and runtime-store scheduled dispatch), received-message
/// creation with poison handling, auto/manual ack settlement, and the resilient consumer/subscription loop.
/// </summary>
internal sealed class MessageClientCore : IAsyncDisposable
{
    private readonly IMessageTransport _transport;
    private readonly ISerializer _serializer;
    private readonly IMessageRouter _router;
    private readonly IJobRuntimeStore? _runtimeStore;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly Func<string, Exception?, Exception> _exceptionFactory;
    private readonly ConcurrentDictionary<string, MessageListenerHandle> _listeners = new(StringComparer.Ordinal);
    private int _isDisposed;

    public MessageClientCore(IMessageTransport transport, ISerializer serializer, IMessageRouter router,
        IJobRuntimeStore? runtimeStore, TimeProvider timeProvider, ILogger logger, Func<string, Exception?, Exception> exceptionFactory)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _serializer = serializer;
        _router = router;
        _runtimeStore = runtimeStore;
        _timeProvider = timeProvider;
        _logger = logger;
        _exceptionFactory = exceptionFactory;
    }

    public IMessageRouter Router => _router;

    public Task EnsureAsync(IReadOnlyList<DestinationDeclaration> declarations, CancellationToken cancellationToken)
    {
        return _transport is ISupportsProvisioning provisioning
            ? provisioning.EnsureAsync(declarations, cancellationToken)
            : Task.CompletedTask;
    }

    public async Task<string> SendAsync(ScheduledDispatchKind kind, Type messageType, object message, MessageEnvelopeOptions options, string destination, Func<string, CancellationToken, Task>? ensureDestination, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ValidateCapabilities(options.Priority, options.TimeToLive);

        var sendOptions = BuildSendOptions(options);
        string messageId = options.DeduplicationId ?? Guid.NewGuid().ToString("N");
        var transportMessage = CreateTransportMessage(message, messageType, options, messageId);

        if (ensureDestination is not null)
            await ensureDestination(destination, cancellationToken).AnyContext();

        if (await TryScheduleAsync(kind, destination, [transportMessage], sendOptions, cancellationToken).AnyContext())
            return messageId;

        var items = await SendChunkedAsync(destination, [transportMessage], sendOptions, cancellationToken).AnyContext();
        var item = items.Count > 0 ? items[0] : null;
        if (item is null || !item.Success)
            throw _exceptionFactory($"Unable to send message to \"{destination}\": {item?.ErrorCode ?? "unknown error"}", null);

        return item.MessageId ?? messageId;
    }

    public async Task SendBatchAsync(ScheduledDispatchKind kind, IEnumerable<object> messages, Type? declaredType, MessageEnvelopeOptions options, Func<Type, string> resolveDestination, Func<string, CancellationToken, Task>? ensureDestination, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ValidateCapabilities(options.Priority, options.TimeToLive);

        var sendOptions = BuildSendOptions(options);
        var grouped = new Dictionary<string, List<TransportMessage>>(StringComparer.Ordinal);
        int index = 0;

        foreach (var message in messages)
        {
            ArgumentNullException.ThrowIfNull(message);
            Type messageType = declaredType ?? message.GetType();
            string destination = resolveDestination(messageType);
            string? messageId = options.DeduplicationId is null ? null : $"{options.DeduplicationId}:{index}";
            index++;

            if (!grouped.TryGetValue(destination, out var transportMessages))
            {
                transportMessages = [];
                grouped.Add(destination, transportMessages);
            }

            transportMessages.Add(CreateTransportMessage(message, messageType, options, messageId));
        }

        foreach (var group in grouped)
        {
            if (ensureDestination is not null)
                await ensureDestination(group.Key, cancellationToken).AnyContext();

            if (await TryScheduleAsync(kind, group.Key, group.Value, sendOptions, cancellationToken).AnyContext())
                continue;

            var items = await SendChunkedAsync(group.Key, group.Value, sendOptions, cancellationToken).AnyContext();
            int failed = items.Count(i => !i.Success);
            if (failed > 0)
                throw _exceptionFactory($"Unable to send {failed} of {items.Count} messages to \"{group.Key}\".", null);
        }
    }

    public async Task<IReceivedMessage?> ReceiveAsync(string source, TimeSpan? maxWaitTime, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var pull = RequirePull();
        var entries = await pull.ReceiveAsync(source, new ReceiveRequest { MaxMessages = 1, MaxWaitTime = maxWaitTime }, cancellationToken).AnyContext();
        return entries.Count == 0 ? null : CreateReceivedMessage(entries[0], cancellationToken);
    }

    public async Task<IReceivedMessage<T>?> ReceiveAsync<T>(string source, TimeSpan? maxWaitTime, CancellationToken cancellationToken) where T : class
    {
        ThrowIfDisposed();
        var pull = RequirePull();
        var entries = await pull.ReceiveAsync(source, new ReceiveRequest { MaxMessages = 1, MaxWaitTime = maxWaitTime }, cancellationToken).AnyContext();
        return entries.Count == 0 ? null : await CreateReceivedMessageAsync<T>(entries[0], cancellationToken).AnyContext();
    }

    public Task<MessageListenerHandle> StartListenerAsync(ListenerConfig config, Func<IReceivedMessage, CancellationToken, Task> handler, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var registration = MessageListenerRegistration.Create(handler, config);
        return StartListenerCoreAsync(config, registration, async (entry, token) =>
        {
            var received = CreateReceivedMessage(entry, token);
            await HandleMessageAsync(received, config, handler, token).AnyContext();
        }, cancellationToken);
    }

    public Task<MessageListenerHandle> StartListenerAsync<T>(ListenerConfig config, Func<IReceivedMessage<T>, CancellationToken, Task> handler, CancellationToken cancellationToken) where T : class
    {
        ArgumentNullException.ThrowIfNull(handler);
        var registration = MessageListenerRegistration.Create(handler, config);
        return StartListenerCoreAsync(config, registration, async (entry, token) =>
        {
            var received = await CreateReceivedMessageAsync<T>(entry, token).AnyContext();
            await HandleMessageAsync(received, config, handler, token).AnyContext();
        }, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) == 1)
            return;

        foreach (var listener in _listeners.Values.ToArray())
            await listener.DisposeAsync().AnyContext();

        await _transport.DisposeAsync().AnyContext();
    }

    private async Task<MessageListenerHandle> StartListenerCoreAsync(ListenerConfig config, MessageListenerRegistration registration, Func<TransportEntry, CancellationToken, Task> onMessage, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        if (_listeners.TryGetValue(config.Key, out var existing) && !existing.IsDisposed)
        {
            existing.ThrowIfConflicting(registration);
            return existing;
        }

        var handle = new MessageListenerHandle(config.Topic, config.Subscription, config.Source, config.Key, registration, RemoveListener);
        if (!_listeners.TryAdd(config.Key, handle))
        {
            await handle.DisposeAsync().AnyContext();
            var current = _listeners[config.Key];
            current.ThrowIfConflicting(registration);
            return current;
        }

        try
        {
            if (_transport is ISupportsPush push)
            {
                var subscription = await push.SubscribeAsync(config.Source, onMessage, new PushOptions { MaxConcurrentMessages = Math.Max(1, config.MaxConcurrency) }, cancellationToken).AnyContext();
                handle.SetPushSubscription(subscription);
                return handle;
            }

            if (_transport is not ISupportsPull pull)
                throw _exceptionFactory($"Transport \"{_transport.GetType().Name}\" does not support receiving messages.", null);

            handle.Start(RunPullLoopAsync(config.Source, pull, onMessage, config.MaxConcurrency, handle.CancellationToken));
            return handle;
        }
        catch
        {
            await handle.DisposeAsync().AnyContext();
            throw;
        }
    }

    // MaxConcurrency bounds the number of in-flight messages processed per receive batch. A failure while receiving
    // or while processing a single entry (including a poison message that was already dead-lettered) must never tear
    // down the loop, otherwise one bad message or a transient transport blip silently stops consumption.
    private async Task RunPullLoopAsync(string source, ISupportsPull pull, Func<TransportEntry, CancellationToken, Task> onMessage, int maxConcurrency, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            IReadOnlyList<TransportEntry> entries;
            try
            {
                entries = await pull.ReceiveAsync(source, new ReceiveRequest
                {
                    MaxMessages = Math.Max(1, maxConcurrency),
                    MaxWaitTime = TimeSpan.FromSeconds(1)
                }, cancellationToken).AnyContext();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error receiving from \"{Source}\"; retrying: {Message}", source, ex.Message);
                await _timeProvider.SafeDelay(TimeSpan.FromSeconds(1), cancellationToken).AnyContext();
                continue;
            }

            var tasks = entries.Select(entry => SafeProcessAsync(entry, onMessage, source, cancellationToken)).ToArray();
            await Task.WhenAll(tasks).AnyContext();
        }
    }

    private async Task SafeProcessAsync(TransportEntry entry, Func<TransportEntry, CancellationToken, Task> onMessage, string source, CancellationToken cancellationToken)
    {
        try
        {
            await onMessage(entry, cancellationToken).AnyContext();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // The message has already been settled (dead-lettered on deserialize failure, abandoned/dead-lettered on
            // handler error); swallowing here keeps the loop alive for the next message.
            _logger.LogError(ex, "Error processing message \"{MessageId}\" from \"{Source}\": {Message}", entry.Id, source, ex.Message);
        }
    }

    private async Task HandleMessageAsync<TMessage>(TMessage message, ListenerConfig config, Func<TMessage, CancellationToken, Task> handler, CancellationToken cancellationToken) where TMessage : IReceivedMessage
    {
        try
        {
            await handler(message, cancellationToken).AnyContext();

            if (config.AckMode == AckMode.Auto && !message.IsHandled)
                await message.CompleteAsync(cancellationToken).AnyContext();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Handler failed for message \"{MessageId}\" from \"{Source}\" (attempt {Attempt} of {MaxAttempts}): {Message}", message.Id, config.Source, message.Attempts, config.MaxAttempts, ex.Message);
            await SettleFailedMessageAsync(message, config, cancellationToken).AnyContext();
        }
    }

    private static async Task SettleFailedMessageAsync(IReceivedMessage message, ListenerConfig config, CancellationToken cancellationToken)
    {
        if (message.IsHandled)
            return;

        if (message.Attempts >= config.MaxAttempts)
        {
            await message.DeadLetterAsync("handler-error", cancellationToken).AnyContext();
            return;
        }

        TimeSpan? redeliveryDelay = config.RedeliveryBackoff?.Invoke(message.Attempts);
        if (redeliveryDelay is { } delay && delay > TimeSpan.Zero && message is ISupportsDelayedMessageAbandon received)
            await received.AbandonAsync(delay, cancellationToken).AnyContext();
        else
            await message.AbandonAsync(cancellationToken).AnyContext();
    }

    private ReceivedMessage CreateReceivedMessage(TransportEntry entry, CancellationToken cancellationToken)
    {
        return new ReceivedMessage(_transport, entry, cancellationToken, _runtimeStore, _timeProvider);
    }

    private async Task<IReceivedMessage<T>> CreateReceivedMessageAsync<T>(TransportEntry entry, CancellationToken cancellationToken) where T : class
    {
        T? message;
        try
        {
            message = _serializer.Deserialize<T>(entry.Body);
        }
        catch (Exception ex)
        {
            await DeadLetterPoisonMessageAsync(entry, "deserialize-failure", cancellationToken).AnyContext();
            throw _exceptionFactory($"Unable to deserialize message \"{entry.Id}\".", ex);
        }

        if (message is null)
        {
            await DeadLetterPoisonMessageAsync(entry, "deserialize-failure", cancellationToken).AnyContext();
            throw _exceptionFactory($"Message \"{entry.Id}\" deserialized to null.", null);
        }

        return new ReceivedMessage<T>(_transport, entry, message, cancellationToken, _runtimeStore, _timeProvider);
    }

    private Task DeadLetterPoisonMessageAsync(TransportEntry entry, string reason, CancellationToken cancellationToken)
    {
        return ReceivedMessage.DeadLetterAsync(_transport, entry, reason, cancellationToken);
    }

    public string ResolveMessageType(Type messageType) => _router.ResolveMessageType(messageType);

    private TransportMessage CreateTransportMessage(object message, Type messageType, MessageEnvelopeOptions options, string? messageId)
    {
        // Content type is intentionally not written as a header: the receive path always uses the single configured
        // serializer, so advertising a per-message content type would be misleading until real negotiation exists.
        var headers = (options.Headers ?? MessageHeaders.Empty).ToBuilder()
            .Set(KnownHeaders.MessageType, _router.ResolveMessageType(messageType))
            .Set(KnownHeaders.Priority, options.Priority.ToString());

        if (!String.IsNullOrEmpty(options.CorrelationId))
            headers.Set(KnownHeaders.CorrelationId, options.CorrelationId);

        if (Activity.Current is { } activity)
        {
            if (!String.IsNullOrEmpty(activity.Id))
                headers.SetIfMissing(KnownHeaders.TraceParent, activity.Id);

            if (!String.IsNullOrEmpty(activity.TraceStateString))
                headers.SetIfMissing(KnownHeaders.TraceState, activity.TraceStateString);
        }

        if (options.TimeToLive is { } ttl)
            headers.Set(KnownHeaders.Expiration, _timeProvider.GetUtcNow().Add(ttl).ToString("O", CultureInfo.InvariantCulture));

        return new TransportMessage
        {
            Body = _serializer.SerializeToBytes(message),
            Headers = headers.Build(),
            MessageId = messageId
        };
    }

    private TransportSendOptions BuildSendOptions(MessageEnvelopeOptions options)
    {
        return new TransportSendOptions
        {
            Priority = options.Priority,
            DeliverAt = options.DeliverAt ?? (options.Delay is { } delay ? _timeProvider.GetUtcNow().Add(delay) : null),
            DeduplicationId = options.DeduplicationId
        };
    }

    private void ValidateCapabilities(MessagePriority priority, TimeSpan? timeToLive)
    {
        if (priority != MessagePriority.Normal && _transport is not ISupportsPriority)
            throw new NotSupportedException($"Transport \"{_transport.GetType().Name}\" does not support message priority.");

        if (timeToLive is not null && _transport is not ISupportsExpiration)
            throw new NotSupportedException($"Transport \"{_transport.GetType().Name}\" does not support message expiration.");
    }

    private async Task<bool> TryScheduleAsync(ScheduledDispatchKind kind, string destination, IReadOnlyList<TransportMessage> messages, TransportSendOptions options, CancellationToken cancellationToken)
    {
        if (!ShouldScheduleThroughRuntimeStore(options, out var dueUtc))
            return false;

        foreach (var message in messages)
        {
            string messageId = message.MessageId ?? Guid.NewGuid().ToString("N");
            await _runtimeStore!.ScheduleDispatchAsync(new ScheduledDispatchState
            {
                DispatchId = messageId,
                Kind = kind,
                Destination = destination,
                Body = message.Body,
                Headers = message.Headers,
                Options = options with { DeliverAt = null },
                DueUtc = dueUtc
            }, cancellationToken).AnyContext();
        }

        return true;
    }

    private bool ShouldScheduleThroughRuntimeStore(TransportSendOptions options, out DateTimeOffset dueUtc)
    {
        dueUtc = options.DeliverAt.GetValueOrDefault();
        if (options.DeliverAt is null || dueUtc <= _timeProvider.GetUtcNow())
            return false;

        if (_transport is ISupportsDelayedDelivery)
            return false;

        if (_runtimeStore is null)
            throw _exceptionFactory($"Delayed delivery requires either native delayed-delivery support from transport \"{_transport.GetType().Name}\" or a registered job runtime store.", null);

        return true;
    }

    private async Task<IReadOnlyList<SendItemResult>> SendChunkedAsync(string destination, IReadOnlyList<TransportMessage> messages, TransportSendOptions options, CancellationToken cancellationToken)
    {
        // Respect a transport-declared maximum batch size by splitting oversized sends into chunks.
        int? maxBatchSize = (_transport as ITransportInfo)?.MaxBatchSize;
        if (maxBatchSize is not { } limit || limit <= 0 || messages.Count <= limit)
        {
            var result = await _transport.SendAsync(destination, messages, options, cancellationToken).AnyContext();
            return result.Items;
        }

        var items = new List<SendItemResult>(messages.Count);
        for (int offset = 0; offset < messages.Count; offset += limit)
        {
            var chunk = messages.Skip(offset).Take(limit).ToArray();
            var result = await _transport.SendAsync(destination, chunk, options, cancellationToken).AnyContext();
            items.AddRange(result.Items);
        }

        return items;
    }

    private ISupportsPull RequirePull()
    {
        return _transport as ISupportsPull
            ?? throw _exceptionFactory($"Transport \"{_transport.GetType().Name}\" does not support pull receive.", null);
    }

    private void RemoveListener(string key, MessageListenerHandle handle)
    {
        _listeners.TryRemove(new KeyValuePair<string, MessageListenerHandle>(key, handle));
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) == 1, this);
    }
}

internal interface ISupportsDelayedMessageAbandon
{
    Task AbandonAsync(TimeSpan redeliveryDelay, CancellationToken cancellationToken = default);
}

internal class ReceivedMessage : IReceivedMessage, ISupportsDelayedMessageAbandon
{
    private readonly IMessageTransport _transport;
    private readonly TransportEntry _entry;
    private readonly IJobRuntimeStore? _runtimeStore;
    private readonly TimeProvider _timeProvider;
    private int _isHandled;

    public ReceivedMessage(IMessageTransport transport, TransportEntry entry, CancellationToken cancellationToken, IJobRuntimeStore? runtimeStore = null, TimeProvider? timeProvider = null)
    {
        _transport = transport;
        _entry = entry;
        _runtimeStore = runtimeStore;
        _timeProvider = timeProvider ?? TimeProvider.System;
        CancellationToken = cancellationToken;
    }

    public string Id => _entry.Id;
    public ReadOnlyMemory<byte> Body => _entry.Body;
    public MessageHeaders Headers => _entry.Headers;
    public string? CorrelationId => Headers.GetValueOrDefault(KnownHeaders.CorrelationId);
    public string? MessageType => Headers.GetValueOrDefault(KnownHeaders.MessageType);
    public MessagePriority Priority => Enum.TryParse(Headers.GetValueOrDefault(KnownHeaders.Priority), ignoreCase: true, out MessagePriority priority) ? priority : MessagePriority.Normal;
    public int Attempts => _entry.DeliveryCount;
    public bool IsHandled => Volatile.Read(ref _isHandled) == 1;
    public CancellationToken CancellationToken { get; }

    public Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        if (!TryMarkHandled())
            return Task.CompletedTask;

        return _transport.CompleteAsync(_entry, cancellationToken);
    }

    public Task AbandonAsync(CancellationToken cancellationToken = default)
    {
        if (!TryMarkHandled())
            return Task.CompletedTask;

        return _transport.AbandonAsync(_entry, cancellationToken);
    }

    public async Task AbandonAsync(TimeSpan redeliveryDelay, CancellationToken cancellationToken = default)
    {
        if (!TryMarkHandled())
            return;

        if (_transport is ISupportsRedeliveryDelay redelivery)
        {
            await redelivery.AbandonAsync(_entry, redeliveryDelay, cancellationToken).AnyContext();
            return;
        }

        if (_runtimeStore is null)
            throw new MessageQueueException($"Delayed redelivery requires either native redelivery-delay support from transport \"{_transport.GetType().Name}\" or a registered job runtime store.");

        int nextAttempt = _entry.DeliveryCount + 1;
        var headers = _entry.Headers.ToBuilder()
            .Set(KnownHeaders.Attempts, nextAttempt.ToString(CultureInfo.InvariantCulture))
            .Build();

        await _runtimeStore.ScheduleDispatchAsync(new ScheduledDispatchState
        {
            DispatchId = $"{_entry.Id}:retry:{nextAttempt}",
            Kind = ScheduledDispatchKind.QueueMessage,
            Destination = _entry.Destination,
            Body = _entry.Body,
            Headers = headers,
            Options = new TransportSendOptions { Priority = Priority },
            DueUtc = _timeProvider.GetUtcNow().Add(redeliveryDelay)
        }, cancellationToken).AnyContext();

        await _transport.CompleteAsync(_entry, cancellationToken).AnyContext();
    }

    public async Task DeadLetterAsync(string? reason = null, CancellationToken cancellationToken = default)
    {
        if (!TryMarkHandled())
            return;

        await DeadLetterAsync(_transport, _entry, reason, cancellationToken).AnyContext();
    }

    public Task RenewLockAsync(TimeSpan? duration = null, CancellationToken cancellationToken = default)
    {
        return _transport is ISupportsLockRenewal lockRenewal
            ? lockRenewal.RenewLockAsync(_entry, duration, cancellationToken)
            : throw new NotSupportedException($"Transport \"{_transport.GetType().Name}\" does not support lock renewal.");
    }

    public Task ReportProgressAsync(int? percent = null, string? message = null, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Message progress reporting requires tracked job execution and is not available for untracked queue or pub/sub messages.");
    }

    internal static async Task DeadLetterAsync(IMessageTransport transport, TransportEntry entry, string? reason, CancellationToken cancellationToken)
    {
        if (transport is not ISupportsDeadLetter deadLetter)
            throw new NotSupportedException($"Transport \"{transport.GetType().Name}\" does not support dead-lettering.");

        await deadLetter.DeadLetterAsync(entry, reason, cancellationToken).AnyContext();
    }

    private bool TryMarkHandled()
    {
        return Interlocked.CompareExchange(ref _isHandled, 1, 0) == 0;
    }
}

internal sealed class ReceivedMessage<T> : ReceivedMessage, IReceivedMessage<T> where T : class
{
    public ReceivedMessage(IMessageTransport transport, TransportEntry entry, T message, CancellationToken cancellationToken, IJobRuntimeStore? runtimeStore = null, TimeProvider? timeProvider = null)
        : base(transport, entry, cancellationToken, runtimeStore, timeProvider)
    {
        Message = message;
    }

    public T Message { get; }
}

internal static class MessageRoutingConventions
{
    public static string ToKebabCase(string value)
    {
        if (String.IsNullOrEmpty(value))
            return value;

        Span<char> buffer = stackalloc char[value.Length * 2];
        int position = 0;
        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            if (Char.IsUpper(current))
            {
                if (index > 0)
                    buffer[position++] = '-';

                buffer[position++] = Char.ToLowerInvariant(current);
            }
            else
            {
                buffer[position++] = current;
            }
        }

        return new String(buffer[..position]);
    }
}

/// <summary>
/// A started listener handle. A single type backs both the queue consumer and pub/sub subscription surfaces; queue
/// callers observe it as <see cref="IMessageConsumer"/> (Source/Key), pub/sub callers as <see cref="IMessageSubscription"/>
/// (Topic/Subscription/Key).
/// </summary>
internal sealed class MessageListenerHandle : IMessageConsumer, IMessageSubscription
{
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly Action<string, MessageListenerHandle> _remove;
    private IPushSubscription? _pushSubscription;
    private Task? _worker;
    private int _isDisposed;

    public MessageListenerHandle(string topic, string subscription, string source, string key, MessageListenerRegistration registration, Action<string, MessageListenerHandle> remove)
    {
        Topic = topic;
        Subscription = subscription;
        Source = source;
        Key = key;
        Registration = registration;
        _remove = remove;
    }

    public string Topic { get; }
    public string Subscription { get; }
    public string Source { get; }
    public string Key { get; }
    public MessageListenerRegistration Registration { get; }
    public CancellationToken CancellationToken => _cancellationTokenSource.Token;
    public bool IsDisposed => Volatile.Read(ref _isDisposed) == 1;

    public void ThrowIfConflicting(MessageListenerRegistration registration)
    {
        if (!Registration.Matches(registration))
            throw new InvalidOperationException($"A listener with key \"{Key}\" is already registered with a different handler or options.");
    }

    public void SetPushSubscription(IPushSubscription subscription)
    {
        _pushSubscription = subscription;
    }

    public void Start(Task worker)
    {
        _worker = worker;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) == 1)
            return;

        await _cancellationTokenSource.CancelAsync().AnyContext();

        if (_pushSubscription is not null)
            await _pushSubscription.DisposeAsync().AnyContext();

        if (_worker is not null)
        {
            try
            {
                await _worker.AnyContext();
            }
            catch (OperationCanceledException) { }
        }

        _cancellationTokenSource.Dispose();
        _remove(Key, this);
    }
}

internal sealed record MessageListenerRegistration
{
    public required Type MessageType { get; init; }
    public required string Source { get; init; }
    public required Delegate Handler { get; init; }
    public required AckMode AckMode { get; init; }
    public required int MaxConcurrency { get; init; }
    public required int MaxAttempts { get; init; }
    public required bool HasRedeliveryBackoff { get; init; }

    public static MessageListenerRegistration Create(Delegate handler, ListenerConfig config)
    {
        return new MessageListenerRegistration
        {
            MessageType = config.MessageType,
            Source = config.Source,
            Handler = handler,
            AckMode = config.AckMode,
            MaxConcurrency = Math.Max(1, config.MaxConcurrency),
            MaxAttempts = config.MaxAttempts,
            HasRedeliveryBackoff = config.RedeliveryBackoff is not null
        };
    }

    public bool Matches(MessageListenerRegistration other)
    {
        return MessageType == other.MessageType
            && String.Equals(Source, other.Source, StringComparison.Ordinal)
            && Handler == other.Handler
            && AckMode == other.AckMode
            && MaxConcurrency == other.MaxConcurrency
            && MaxAttempts == other.MaxAttempts
            && HasRedeliveryBackoff == other.HasRedeliveryBackoff;
    }
}
