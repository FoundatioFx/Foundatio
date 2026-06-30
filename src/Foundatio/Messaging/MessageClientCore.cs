using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
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
/// Core-owned messaging instruments. Counters and histograms are transport-agnostic and shared by every
/// <see cref="MessageQueue"/> and <see cref="PubSub"/> instance so that send/receive/settlement volume and handler
/// latency are observable regardless of which transport is plugged in.
/// </summary>
internal static class MessagingInstruments
{
    public static readonly Counter<long> Sent = FoundatioDiagnostics.Meter.CreateCounter<long>("foundatio.messaging.sent", description: "Number of messages sent to a destination");
    public static readonly Counter<long> Received = FoundatioDiagnostics.Meter.CreateCounter<long>("foundatio.messaging.received", description: "Number of messages received from a source");
    public static readonly Counter<long> Completed = FoundatioDiagnostics.Meter.CreateCounter<long>("foundatio.messaging.completed", description: "Number of messages completed");
    public static readonly Counter<long> Abandoned = FoundatioDiagnostics.Meter.CreateCounter<long>("foundatio.messaging.abandoned", description: "Number of messages abandoned");
    public static readonly Counter<long> DeadLettered = FoundatioDiagnostics.Meter.CreateCounter<long>("foundatio.messaging.deadlettered", description: "Number of messages dead-lettered");
    public static readonly Counter<long> Unhandled = FoundatioDiagnostics.Meter.CreateCounter<long>("foundatio.messaging.unhandled", description: "Number of received messages with no registered consumer for their type");
    public static readonly Histogram<double> HandlerTime = FoundatioDiagnostics.Meter.CreateHistogram<double>("foundatio.messaging.handlertime", unit: "ms", description: "Message handler execution time");
}

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
    // Null falls back to the client's default RetryPolicy.
    public int? MaxAttempts { get; init; }
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
    private readonly RetryPolicy _retryPolicy;
    private readonly IMessageTypeRegistry _typeRegistry;
    private readonly string? _contentType;
    private readonly bool _ownsTransport;
    private readonly ConcurrentDictionary<string, SourceListener> _sources = new(StringComparer.Ordinal);
    private int _isDisposed;

    public MessageClientCore(IMessageTransport transport, ISerializer serializer, IMessageRouter router,
        IJobRuntimeStore? runtimeStore, TimeProvider timeProvider, ILogger logger, Func<string, Exception?, Exception> exceptionFactory, RetryPolicy? retryPolicy = null, bool ownsTransport = true, IMessageTypeRegistry? typeRegistry = null, string? contentType = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _serializer = serializer;
        _router = router;
        _runtimeStore = runtimeStore;
        _timeProvider = timeProvider;
        _logger = logger;
        _exceptionFactory = exceptionFactory;
        _retryPolicy = retryPolicy ?? new RetryPolicy();
        _typeRegistry = typeRegistry ?? new MessageTypeRegistry();
        _contentType = contentType;
        _ownsTransport = ownsTransport;
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

        var sendOptions = BuildSendOptions(options) with { DestinationRole = RoleFor(kind) };
        string messageId = options.DeduplicationId ?? Guid.NewGuid().ToString("N");
        var transportMessage = CreateTransportMessage(message, messageType, options, messageId);

        if (ensureDestination is not null)
            await ensureDestination(destination, cancellationToken).AnyContext();

        if (await TryScheduleAsync(kind, destination, [transportMessage], sendOptions, cancellationToken).AnyContext())
            return messageId;

        // Send is throw-on-failure: SendChunkedAsync propagates any transport error, so a returned result means the
        // message was accepted. Fall back to the pre-assigned id if the transport reported none.
        var items = await SendChunkedAsync(destination, [transportMessage], sendOptions, cancellationToken).AnyContext();
        return (items.Count > 0 ? items[0].MessageId : null) ?? messageId;
    }

    public async Task SendBatchAsync(ScheduledDispatchKind kind, IEnumerable<object> messages, Type? declaredType, MessageEnvelopeOptions options, Func<Type, string> resolveDestination, Func<string, CancellationToken, Task>? ensureDestination, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ValidateCapabilities(options.Priority, options.TimeToLive);

        var sendOptions = BuildSendOptions(options) with { DestinationRole = RoleFor(kind) };
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

            // Send is throw-on-failure (SendChunkedAsync propagates any transport error); a returned result means all
            // messages in this destination group were accepted.
            await SendChunkedAsync(group.Key, group.Value, sendOptions, cancellationToken).AnyContext();
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
        return RegisterConsumerAsync(config, handler, async (entry, token) =>
        {
            var received = CreateReceivedMessage(entry, token);
            await HandleMessageAsync(received, config, handler, token).AnyContext();
        }, cancellationToken);
    }

    public Task<MessageListenerHandle> StartListenerAsync<T>(ListenerConfig config, Func<IReceivedMessage<T>, CancellationToken, Task> handler, CancellationToken cancellationToken) where T : class
    {
        ArgumentNullException.ThrowIfNull(handler);
        return RegisterConsumerAsync(config, handler, async (entry, token) =>
        {
            var received = await CreateReceivedMessageAsync<T>(entry, token).AnyContext();
            await HandleMessageAsync(received, config, handler, token).AnyContext();
        }, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) == 1)
            return;

        foreach (var listener in _sources.Values.ToArray())
            await listener.DisposeAsync().AnyContext();

        // Only dispose the transport when this client owns it. In DI the transport is a shared singleton owned by the
        // container, so neither the queue nor the pub/sub client should dispose it (that would double-dispose the one
        // the other still depends on).
        if (_ownsTransport)
            await _transport.DisposeAsync().AnyContext();
    }

    // Multiple typed consumers can share one destination. They attach to a single per-source listener whose loop
    // demultiplexes each message to the consumer registered for its type; a type with no registered consumer is
    // handled by HandleUnmatchedAsync. Starting the same consumer key with a matching handler/options is idempotent.
    private async Task<MessageListenerHandle> RegisterConsumerAsync(ListenerConfig config, Delegate handler, Func<TransportEntry, CancellationToken, Task> dispatch, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        bool catchAll = IsCatchAll(config.MessageType);
        var registration = new ConsumerRegistration
        {
            Key = config.Key,
            Config = config,
            Dispatch = dispatch,
            Info = MessageListenerRegistration.Create(handler, config),
            IsCatchAll = catchAll,
            TypeName = catchAll ? null : _typeRegistry.GetName(config.MessageType)
        };

        while (true)
        {
            var listener = _sources.GetOrAdd(config.Source, source => new SourceListener(this, source));
            if (listener.TryAddConsumer(registration, out var handle, out bool created))
            {
                if (created)
                {
                    try
                    {
                        await listener.StartAsync(cancellationToken).AnyContext();
                    }
                    catch
                    {
                        await listener.DisposeAsync().AnyContext();
                        throw;
                    }
                }

                return handle;
            }

            // The listener was disposing as its last consumer detached; drop our stale reference and retry.
            _sources.TryRemove(new KeyValuePair<string, SourceListener>(config.Source, listener));
        }
    }

    // A concrete message type binds an exact-type consumer; object/interface/abstract route types are catch-alls that
    // receive every message a more specific typed consumer did not claim (the grouped/raw-envelope path).
    private static bool IsCatchAll(Type messageType)
    {
        return messageType == typeof(object) || messageType.IsInterface || messageType.IsAbstract;
    }

    private async Task HandleUnmatchedAsync(TransportEntry entry, string source, CancellationToken cancellationToken)
    {
        MessagingInstruments.Unhandled.Add(1, new KeyValuePair<string, object?>("source", source));

        var message = CreateReceivedMessage(entry, cancellationToken);

        // Retry so a node that does handle this type can pick it up; dead-letter as "no-handler" once the lenient
        // budget is exhausted so a genuinely orphaned type cannot loop forever.
        await SettleFailedMessageAsync(message, _retryPolicy.UnmatchedMaxAttempts, _retryPolicy.UnmatchedBackoff, deadLetterReason: "no-handler", cancellationToken).AnyContext();

        // Surface loudly. The throw is caught by the loop's per-message handling (SafeProcessAsync), so it never tears
        // down the receive loop or the other type handlers sharing this source.
        throw new UnhandledMessageTypeException(message.MessageType, source);
    }

    // MaxConcurrency bounds the number of in-flight messages. A slot is held from receive until the message settles
    // and is released the instant that one message finishes — so a single slow message never stalls the other slots
    // (no head-of-line blocking) and steady-state utilization stays at the configured concurrency. A failure while
    // receiving or while processing a single entry (including a poison message that was already dead-lettered) must
    // never tear down the loop, otherwise one bad message or a transient transport blip silently stops consumption.
    private async Task RunPullLoopAsync(string source, ISupportsPull pull, Func<TransportEntry, CancellationToken, Task> onMessage, int maxConcurrency, CancellationToken cancellationToken)
    {
        maxConcurrency = Math.Max(1, maxConcurrency);
        var slots = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        var inFlight = new ConcurrentDictionary<Task, byte>();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Block for a free slot before receiving so we never pull more than we can process concurrently.
                try
                {
                    await slots.WaitAsync(cancellationToken).AnyContext();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                // Opportunistically claim any other idle slots so a transport that supports batch receive can still
                // pull a batch while keeping per-message slot release. WaitAsync(Zero) is a non-blocking try-acquire.
                int claimed = 1;
                while (claimed < maxConcurrency && await slots.WaitAsync(TimeSpan.Zero).AnyContext())
                    claimed++;

                IReadOnlyList<TransportEntry> entries;
                try
                {
                    entries = await pull.ReceiveAsync(source, new ReceiveRequest
                    {
                        MaxMessages = claimed,
                        MaxWaitTime = TimeSpan.FromSeconds(1)
                    }, cancellationToken).AnyContext();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    ReleaseSlots(slots, claimed);
                    break;
                }
                catch (Exception ex)
                {
                    ReleaseSlots(slots, claimed);
                    _logger.LogError(ex, "Error receiving from \"{Source}\"; retrying: {Message}", source, ex.Message);
                    await _timeProvider.SafeDelay(TimeSpan.FromSeconds(1), cancellationToken).AnyContext();
                    continue;
                }

                // Return any slots we claimed but didn't fill (empty receive or a partial batch).
                ReleaseSlots(slots, claimed - entries.Count);

                foreach (var entry in entries)
                {
                    var task = ProcessAndReleaseSlotAsync(entry, onMessage, source, slots, cancellationToken);
                    if (!task.IsCompleted)
                    {
                        inFlight[task] = 0;
                        _ = task.ContinueWith(static (t, state) => ((ConcurrentDictionary<Task, byte>)state!).TryRemove(t, out _), inFlight, TaskScheduler.Default);
                    }
                }
            }
        }
        finally
        {
            // Drain in-flight handlers before the semaphore is disposed so their slot releases never hit a disposed handle.
            await Task.WhenAll(inFlight.Keys.ToArray()).AnyContext();
            slots.Dispose();
        }
    }

    private async Task ProcessAndReleaseSlotAsync(TransportEntry entry, Func<TransportEntry, CancellationToken, Task> onMessage, string source, SemaphoreSlim slots, CancellationToken cancellationToken)
    {
        try
        {
            await SafeProcessAsync(entry, onMessage, source, cancellationToken).AnyContext();
        }
        finally
        {
            slots.Release();
        }
    }

    private static void ReleaseSlots(SemaphoreSlim slots, int count)
    {
        if (count > 0)
            slots.Release(count);
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
        // Re-establish the producer's trace context on the consumer side so a cross-process trace continues here
        // instead of breaking at the transport boundary.
        using var activity = StartProcessActivity(message, config);
        long startTimestamp = Stopwatch.GetTimestamp();
        try
        {
            await handler(message, cancellationToken).AnyContext();

            if (config.AckMode == AckMode.Auto && !message.IsHandled)
                await message.CompleteAsync(cancellationToken).AnyContext();
        }
        catch (Exception ex)
        {
            activity?.SetErrorStatus(ex);
            int maxAttempts = config.MaxAttempts ?? _retryPolicy.MaxAttempts;
            var backoff = config.RedeliveryBackoff ?? _retryPolicy.Backoff;
            _logger.LogError(ex, "Handler failed for message \"{MessageId}\" from \"{Source}\" (attempt {Attempt} of {MaxAttempts}): {Message}", message.Id, config.Source, message.Attempts, maxAttempts, ex.Message);
            await SettleFailedMessageAsync(message, maxAttempts, backoff, "handler-error", cancellationToken).AnyContext();
        }
        finally
        {
            MessagingInstruments.HandlerTime.Record(Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds, new KeyValuePair<string, object?>("source", config.Source));
        }
    }

    private static Activity? StartProcessActivity(IReceivedMessage message, ListenerConfig config)
    {
        string? traceParent = message.Headers.GetValueOrDefault(KnownHeaders.TraceParent);
        var activity = FoundatioDiagnostics.ActivitySource.StartActivity("ProcessMessage", ActivityKind.Consumer, traceParent);
        if (activity is null)
            return null;

        string? traceState = message.Headers.GetValueOrDefault(KnownHeaders.TraceState);
        if (!String.IsNullOrEmpty(traceState))
            activity.TraceStateString = traceState;

        activity.DisplayName = $"Process: {message.MessageType ?? config.MessageType.Name}";

        if (activity.IsAllDataRequested)
        {
            activity.SetTag("messaging.source", config.Source);
            activity.SetTag("messaging.message.id", message.Id);
        }

        return activity;
    }

    private static Task SettleFailedMessageAsync(IReceivedMessage message, int maxAttempts, Func<int, TimeSpan>? backoff, string deadLetterReason, CancellationToken cancellationToken)
    {
        if (message.IsHandled)
            return Task.CompletedTask;

        if (message.Attempts >= maxAttempts)
            return message.RejectAsync(new RejectOptions { Terminal = true, Reason = deadLetterReason }, cancellationToken);

        return message.RejectAsync(new RejectOptions { RedeliveryDelay = backoff?.Invoke(message.Attempts) }, cancellationToken);
    }

    private ReceivedMessage CreateReceivedMessage(TransportEntry entry, CancellationToken cancellationToken)
    {
        MessagingInstruments.Received.Add(1, new KeyValuePair<string, object?>("source", entry.Destination));
        return new ReceivedMessage(_transport, entry, cancellationToken, _runtimeStore, _timeProvider, _retryPolicy.DeadLetterDestination);
    }

    private async Task<IReceivedMessage<T>> CreateReceivedMessageAsync<T>(TransportEntry entry, CancellationToken cancellationToken) where T : class
    {
        MessagingInstruments.Received.Add(1, new KeyValuePair<string, object?>("source", entry.Destination));

        // For an interface/base route the body cannot be deserialized as T directly. Resolve the concrete payload type
        // from the message-type header via the registry and deserialize that, then hand it back as T (the concrete
        // instance is assignable to T). Exact concrete routes deserialize as T directly.
        Type targetType = typeof(T);
        if (typeof(T).IsInterface || typeof(T).IsAbstract)
        {
            string? typeName = entry.Headers.GetValueOrDefault(KnownHeaders.MessageType);
            var resolved = String.IsNullOrEmpty(typeName) ? null : _typeRegistry.Resolve(typeName);
            if (resolved is null || !typeof(T).IsAssignableFrom(resolved))
            {
                await DeadLetterPoisonMessageAsync(entry, "unresolved-type", cancellationToken).AnyContext();
                throw _exceptionFactory($"Unable to resolve a concrete type \"{typeName}\" assignable to \"{typeof(T).Name}\" for message \"{entry.Id}\".", null);
            }

            targetType = resolved;
        }

        T? message;
        try
        {
            message = _serializer.Deserialize(entry.Body, targetType) as T;
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

        return new ReceivedMessage<T>(_transport, entry, message, cancellationToken, _runtimeStore, _timeProvider, _retryPolicy.DeadLetterDestination);
    }

    private Task DeadLetterPoisonMessageAsync(TransportEntry entry, string reason, CancellationToken cancellationToken)
    {
        MessagingInstruments.DeadLettered.Add(1, new KeyValuePair<string, object?>("source", entry.Destination));
        return ReceivedMessage.DeadLetterOrDropAsync(_transport, entry, reason, _retryPolicy.DeadLetterDestination, cancellationToken);
    }


    private TransportMessage CreateTransportMessage(object message, Type messageType, MessageEnvelopeOptions options, string? messageId)
    {
        // Content type is intentionally not written as a header: the receive path always uses the single configured
        // serializer, so advertising a per-message content type would be misleading until real negotiation exists.
        var headers = (options.Headers ?? MessageHeaders.Empty).ToBuilder()
            .Set(KnownHeaders.MessageType, _typeRegistry.GetName(messageType))
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
            MessageId = messageId,
            ContentType = _contentType
        };
    }

    // A pub/sub publish targets a topic; everything else targets a queue. Stating the role lets the transport route
    // without inferring (e.g. SNS publish vs. SQS send).
    private static DestinationRole RoleFor(ScheduledDispatchKind kind)
    {
        return kind == ScheduledDispatchKind.PubSubMessage ? DestinationRole.Topic : DestinationRole.Queue;
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
        var now = _timeProvider.GetUtcNow();
        if (options.DeliverAt is null || dueUtc <= now)
            return false;

        // A transport can deliver natively only up to its advertised maximum; a delay longer than the broker supports
        // (e.g. SQS caps DelaySeconds at 15 minutes) must route through the durable runtime store rather than be
        // silently truncated to the broker's ceiling.
        if (_transport is ISupportsDelayedDelivery delayed && (delayed.MaxDeliveryDelay is not { } max || dueUtc - now <= max))
            return false;

        if (_runtimeStore is null)
            throw _exceptionFactory($"Delayed delivery requires either native delayed-delivery support from transport \"{_transport.GetType().Name}\" (within its supported maximum) or a registered job runtime store.", null);

        return true;
    }

    private async Task<IReadOnlyList<SendItemResult>> SendChunkedAsync(string destination, IReadOnlyList<TransportMessage> messages, TransportSendOptions options, CancellationToken cancellationToken)
    {
        // Respect a transport-declared maximum batch size by splitting oversized sends into chunks.
        int? maxBatchSize = (_transport as ITransportInfo)?.MaxBatchSize;
        if (maxBatchSize is not { } limit || limit <= 0 || messages.Count <= limit)
        {
            var result = await _transport.SendAsync(destination, messages, options, cancellationToken).AnyContext();
            RecordSent(destination, result.Items);
            return result.Items;
        }

        var items = new List<SendItemResult>(messages.Count);
        for (int offset = 0; offset < messages.Count; offset += limit)
        {
            var chunk = messages.Skip(offset).Take(limit).ToArray();
            var result = await _transport.SendAsync(destination, chunk, options, cancellationToken).AnyContext();
            RecordSent(destination, result.Items);
            items.AddRange(result.Items);
        }

        return items;
    }

    private static void RecordSent(string destination, IReadOnlyList<SendItemResult> items)
    {
        // Every returned item was accepted (send is throw-on-failure).
        if (items.Count > 0)
            MessagingInstruments.Sent.Add(items.Count, new KeyValuePair<string, object?>("destination", destination));
    }

    private ISupportsPull RequirePull()
    {
        return _transport as ISupportsPull
            ?? throw _exceptionFactory($"Transport \"{_transport.GetType().Name}\" does not support pull receive.", null);
    }

    private void RemoveSource(string source, SourceListener listener)
    {
        _sources.TryRemove(new KeyValuePair<string, SourceListener>(source, listener));
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) == 1, this);
    }

    private sealed class ConsumerRegistration
    {
        public required string Key { get; init; }
        public required ListenerConfig Config { get; init; }
        public required Func<TransportEntry, CancellationToken, Task> Dispatch { get; init; }
        public required MessageListenerRegistration Info { get; init; }
        public required bool IsCatchAll { get; init; }
        public required string? TypeName { get; init; }
    }

    // One receive loop per source. Consumers register by message type; the loop reads the message-type header and
    // dispatches each entry to a consumer for that type (round-robin when several share a type, so same-type consumers
    // compete), to the catch-all group for unmapped types, or to HandleUnmatchedAsync when nothing claims the type.
    // The loop runs while at least one consumer is attached and shuts down when the last one detaches.
    private sealed class SourceListener
    {
        private readonly MessageClientCore _core;
        private readonly string _source;
        private readonly object _lock = new();
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private readonly ConcurrentDictionary<string, Registered> _consumers = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, ConsumerGroup> _byType = new(StringComparer.Ordinal);
        private readonly ConsumerGroup _catchAll = new();
        private int _maxConcurrency = 1;
        private IPushSubscription? _pushSubscription;
        private Task? _loop;
        private bool _isDisposed;

        public SourceListener(MessageClientCore core, string source)
        {
            _core = core;
            _source = source;
        }

        public bool TryAddConsumer(ConsumerRegistration registration, out MessageListenerHandle handle, out bool created)
        {
            handle = null!;
            created = false;

            lock (_lock)
            {
                if (_isDisposed)
                    return false;

                if (_consumers.TryGetValue(registration.Key, out var existing))
                {
                    if (!existing.Registration.Info.Matches(registration.Info))
                        throw new InvalidOperationException($"A consumer with key \"{registration.Key}\" is already registered with a different handler or options.");

                    handle = existing.Handle; // idempotent re-registration
                    return true;
                }

                int desired = Math.Max(1, registration.Config.MaxConcurrency);
                if (_consumers.IsEmpty)
                {
                    _maxConcurrency = desired;
                    created = true;
                }
                else if (desired != _maxConcurrency)
                {
                    throw new InvalidOperationException($"Source \"{_source}\" is already consumed with MaxConcurrency {_maxConcurrency}; a conflicting MaxConcurrency {desired} was requested. Consumers sharing a destination must use the same MaxConcurrency.");
                }

                handle = new MessageListenerHandle(registration.Config.Topic, registration.Config.Subscription, _source, registration.Key, () => RemoveConsumerAsync(registration.Key));
                _consumers[registration.Key] = new Registered(registration, handle);
                GroupFor(registration).Add(registration);

                return true;
            }
        }

        private ConsumerGroup GroupFor(ConsumerRegistration registration)
        {
            return registration.IsCatchAll ? _catchAll : _byType.GetOrAdd(registration.TypeName!, _ => new ConsumerGroup());
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (_core._transport is ISupportsPush push)
            {
                // Route the push callback through SafeProcessAsync so a throw (including an unmatched-type throw) is
                // isolated to the message and never tears down the subscription.
                _pushSubscription = await push.SubscribeAsync(_source, (entry, token) => _core.SafeProcessAsync(entry, DispatchAsync, _source, token), new PushOptions { MaxConcurrentMessages = Math.Max(1, _maxConcurrency) }, cancellationToken).AnyContext();
                return;
            }

            if (_core._transport is not ISupportsPull pull)
                throw _core._exceptionFactory($"Transport \"{_core._transport.GetType().Name}\" does not support receiving messages.", null);

            _loop = _core.RunPullLoopAsync(_source, pull, DispatchAsync, _maxConcurrency, _cancellationTokenSource.Token);
        }

        public async ValueTask DisposeAsync()
        {
            lock (_lock)
            {
                if (_isDisposed)
                    return;

                _isDisposed = true;
            }

            await ShutdownAsync().AnyContext();
        }

        private async ValueTask RemoveConsumerAsync(string key)
        {
            bool shutdown = false;
            lock (_lock)
            {
                if (!_consumers.TryRemove(key, out var registered))
                    return;

                var registration = registered.Registration;
                if (registration.IsCatchAll)
                {
                    _catchAll.Remove(registration);
                }
                else if (registration.TypeName is { } typeName && _byType.TryGetValue(typeName, out var group))
                {
                    group.Remove(registration);
                    if (group.IsEmpty)
                        _byType.TryRemove(new KeyValuePair<string, ConsumerGroup>(typeName, group));
                }

                if (_consumers.IsEmpty && !_isDisposed)
                {
                    _isDisposed = true;
                    shutdown = true;
                }
            }

            if (shutdown)
                await ShutdownAsync().AnyContext();
        }

        private async Task ShutdownAsync()
        {
            await _cancellationTokenSource.CancelAsync().AnyContext();

            if (_pushSubscription is not null)
                await _pushSubscription.DisposeAsync().AnyContext();

            if (_loop is not null)
            {
                try
                {
                    await _loop.AnyContext();
                }
                catch (OperationCanceledException) { }
            }

            _cancellationTokenSource.Dispose();
            _core.RemoveSource(_source, this);
        }

        private async Task DispatchAsync(TransportEntry entry, CancellationToken token)
        {
            var registration = Resolve(entry);
            if (registration is null)
            {
                await _core.HandleUnmatchedAsync(entry, _source, token).AnyContext();
                return;
            }

            await registration.Dispatch(entry, token).AnyContext();
        }

        private ConsumerRegistration? Resolve(TransportEntry entry)
        {
            string? typeName = entry.Headers.GetValueOrDefault(KnownHeaders.MessageType);
            if (typeName is not null && _byType.TryGetValue(typeName, out var group) && group.Next() is { } typed)
                return typed;

            return _catchAll.Next();
        }

        private sealed record Registered(ConsumerRegistration Registration, MessageListenerHandle Handle);

        // Consumers sharing a message type (or the catch-all) on one source compete: each message is dispatched to one
        // of them, round-robin. The registration array is swapped under the listener lock; Next() reads it lock-free.
        private sealed class ConsumerGroup
        {
            private ConsumerRegistration[] _registrations = [];
            private int _next;

            public bool IsEmpty => Volatile.Read(ref _registrations).Length == 0;

            public void Add(ConsumerRegistration registration)
            {
                var current = _registrations;
                var updated = new ConsumerRegistration[current.Length + 1];
                Array.Copy(current, updated, current.Length);
                updated[^1] = registration;
                Volatile.Write(ref _registrations, updated);
            }

            public void Remove(ConsumerRegistration registration)
            {
                var current = _registrations;
                int index = Array.IndexOf(current, registration);
                if (index < 0)
                    return;

                var updated = new ConsumerRegistration[current.Length - 1];
                Array.Copy(current, 0, updated, 0, index);
                Array.Copy(current, index + 1, updated, index, current.Length - index - 1);
                Volatile.Write(ref _registrations, updated);
            }

            public ConsumerRegistration? Next()
            {
                var snapshot = Volatile.Read(ref _registrations);
                if (snapshot.Length == 0)
                    return null;
                if (snapshot.Length == 1)
                    return snapshot[0];

                int index = (int)((uint)Interlocked.Increment(ref _next) % (uint)snapshot.Length);
                return snapshot[index];
            }
        }
    }
}

internal class ReceivedMessage : IReceivedMessage
{
    private readonly IMessageTransport _transport;
    private readonly TransportEntry _entry;
    private readonly IJobRuntimeStore? _runtimeStore;
    private readonly TimeProvider _timeProvider;
    private readonly string? _deadLetterDestination;
    private int _isHandled;

    public ReceivedMessage(IMessageTransport transport, TransportEntry entry, CancellationToken cancellationToken, IJobRuntimeStore? runtimeStore = null, TimeProvider? timeProvider = null, string? deadLetterDestination = null)
    {
        _transport = transport;
        _entry = entry;
        _runtimeStore = runtimeStore;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _deadLetterDestination = deadLetterDestination;
        CancellationToken = cancellationToken;
    }

    public string Id => _entry.Id;
    public ReadOnlyMemory<byte> Body => _entry.Body;
    public MessageHeaders Headers => _entry.Headers;
    public string? CorrelationId => Headers.GetValueOrDefault(KnownHeaders.CorrelationId);
    public string? MessageType => Headers.GetValueOrDefault(KnownHeaders.MessageType);
    public MessagePriority Priority => Enum.TryParse(Headers.GetValueOrDefault(KnownHeaders.Priority), ignoreCase: true, out MessagePriority priority) ? priority : MessagePriority.Normal;

    // Reconcile the transport-reported delivery count with the message.attempts header. When redelivery-delay is
    // served through the runtime-store fallback (transports without native ISupportsRedeliveryDelay), the message is
    // re-sent as a brand-new transport message, so its DeliveryCount resets to 1; the carried-over attempt count
    // lives in the header. Taking the max keeps MaxAttempts/dead-letter correct regardless of whether the transport
    // honors the header, so the counter never silently resets and redelivery can't loop forever.
    public int Attempts => Math.Max(_entry.DeliveryCount, ParseAttemptsHeader(_entry.Headers));
    public bool IsHandled => Volatile.Read(ref _isHandled) == 1;
    public CancellationToken CancellationToken { get; }

    public Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        if (!TryMarkHandled())
            return Task.CompletedTask;

        MessagingInstruments.Completed.Add(1, new KeyValuePair<string, object?>("source", _entry.Destination));
        return _transport.CompleteAsync(_entry, cancellationToken);
    }

    public async Task RejectAsync(RejectOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (!TryMarkHandled())
            return;

        options ??= new RejectOptions();

        if (options.Terminal)
        {
            MessagingInstruments.DeadLettered.Add(1, new KeyValuePair<string, object?>("source", _entry.Destination));
            await DeadLetterOrDropAsync(_transport, _entry, options.Reason, _deadLetterDestination, cancellationToken).AnyContext();
            return;
        }

        MessagingInstruments.Abandoned.Add(1, new KeyValuePair<string, object?>("source", _entry.Destination));

        if (options.RedeliveryDelay is not { } redeliveryDelay || redeliveryDelay <= TimeSpan.Zero)
        {
            await _transport.AbandonAsync(_entry, cancellationToken).AnyContext();
            return;
        }

        // Honor an explicit redelivery delay natively when the transport can (within its advertised maximum); otherwise
        // re-schedule the message through the runtime store and complete the original so the delay survives transports
        // without native delayed redelivery.
        if (_transport is ISupportsRedeliveryDelay redelivery && (redelivery.MaxRedeliveryDelay is not { } max || redeliveryDelay <= max))
        {
            await redelivery.AbandonAsync(_entry, redeliveryDelay, cancellationToken).AnyContext();
            return;
        }

        if (_runtimeStore is null)
            throw new MessageQueueException($"Delayed redelivery requires either native redelivery-delay support from transport \"{_transport.GetType().Name}\" (within its supported maximum) or a registered job runtime store.");

        // Advance from the reconciled attempt count, not the raw transport DeliveryCount: the re-send produces a new
        // transport message whose native DeliveryCount resets to 1, so basing the next attempt on DeliveryCount would
        // pin it at 2 and redeliver forever. Attempts already takes the max of DeliveryCount and the carried header.
        int nextAttempt = Attempts + 1;
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

    public Task RenewLockAsync(TimeSpan? duration = null, CancellationToken cancellationToken = default)
    {
        return _transport is ISupportsLockRenewal lockRenewal
            ? lockRenewal.RenewLockAsync(_entry, duration, cancellationToken)
            : throw new NotSupportedException($"Transport \"{_transport.GetType().Name}\" does not support lock renewal.");
    }

    // Terminal settlement. Prefer the transport's native dead-letter sink (preserves native DLQ tooling). When the
    // transport has none, fall back to a configured core-managed dead-letter destination: copy the raw entry there
    // (recording the reason) and complete the original. With neither, the message can't be parked, so it is completed
    // (dropped) rather than throwing and stalling the consumer.
    internal static async Task DeadLetterOrDropAsync(IMessageTransport transport, TransportEntry entry, string? reason, string? deadLetterDestination, CancellationToken cancellationToken)
    {
        if (transport is ISupportsDeadLetter deadLetter)
        {
            await deadLetter.DeadLetterAsync(entry, reason, cancellationToken).AnyContext();
            return;
        }

        if (!String.IsNullOrEmpty(deadLetterDestination))
        {
            var headers = String.IsNullOrEmpty(reason)
                ? entry.Headers
                : entry.Headers.ToBuilder().Set(KnownHeaders.DeadLetterReason, reason).Build();
            await transport.SendAsync(deadLetterDestination, [new TransportMessage { Body = entry.Body, Headers = headers, MessageId = entry.Id }], new TransportSendOptions(), cancellationToken).AnyContext();
            await transport.CompleteAsync(entry, cancellationToken).AnyContext();
            return;
        }

        await transport.CompleteAsync(entry, cancellationToken).AnyContext();
    }

    private bool TryMarkHandled()
    {
        return Interlocked.CompareExchange(ref _isHandled, 1, 0) == 0;
    }

    private static int ParseAttemptsHeader(MessageHeaders headers)
    {
        return Int32.TryParse(headers.GetValueOrDefault(KnownHeaders.Attempts), NumberStyles.Integer, CultureInfo.InvariantCulture, out int attempts) && attempts > 0
            ? attempts
            : 0;
    }
}

internal sealed class ReceivedMessage<T> : ReceivedMessage, IReceivedMessage<T> where T : class
{
    public ReceivedMessage(IMessageTransport transport, TransportEntry entry, T message, CancellationToken cancellationToken, IJobRuntimeStore? runtimeStore = null, TimeProvider? timeProvider = null, string? deadLetterDestination = null)
        : base(transport, entry, cancellationToken, runtimeStore, timeProvider, deadLetterDestination)
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
    private readonly Func<ValueTask> _dispose;
    private int _isDisposed;

    public MessageListenerHandle(string topic, string subscription, string source, string key, Func<ValueTask> dispose)
    {
        Topic = topic;
        Subscription = subscription;
        Source = source;
        Key = key;
        _dispose = dispose;
    }

    public string Topic { get; }
    public string Subscription { get; }
    public string Source { get; }
    public string Key { get; }

    // Disposing a single consumer handle detaches just that consumer from its source listener; the underlying receive
    // loop keeps running until its last consumer detaches.
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) == 1)
            return;

        await _dispose().AnyContext();
    }
}

internal sealed record MessageListenerRegistration
{
    public required Type MessageType { get; init; }
    public required string Source { get; init; }
    public required Delegate Handler { get; init; }
    public required AckMode AckMode { get; init; }
    public required int MaxConcurrency { get; init; }
    public required int? MaxAttempts { get; init; }
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
