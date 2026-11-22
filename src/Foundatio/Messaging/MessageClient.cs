using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging.Transport;
using Foundatio.Serializer;
using Foundatio.Testing;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;

namespace Foundatio.Messaging;

internal sealed class MessageClient : IMessageClient
{
    private const int MaxReportedMessageTypes = 1024;
    private static readonly int[] FirstIndex = [0];

    private readonly MessagingRuntime _runtime;
    private readonly ILogger _logger;
    private readonly IReadOnlyList<MessageHandlerRegistration> _handlers;
    private readonly ConcurrentDictionary<string, byte> _reportedUnsupportedOptions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Type, byte> _checkedSends = new();
    private readonly ConcurrentDictionary<Type, byte> _checkedPublishes = new();

    /// <param name="runtime">The runtime it sends on.</param>
    /// <param name="handlers">This app's registered handlers, to warn when a message is sent to the wrong kind of handler.</param>
    public MessageClient(MessagingRuntime runtime, IReadOnlyList<MessageHandlerRegistration>? handlers = null)
    {
        _runtime = runtime;
        _handlers = handlers ?? [];
        _logger = runtime.LoggerFactory.CreateLogger<MessageClient>();
    }

    public Task<string> SendAsync<T>(T message, SendOptions? options = null, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(message);
        string id = options?.MessageId ?? NewMessageId();
        return SendCoreAsync(MessageDestinationKind.Queue, [message], [id], null, options, options?.Queue, isBatch: false, id, cancellationToken);
    }

    public Task<IReadOnlyList<string>> SendBatchAsync<T>(IEnumerable<T> messages, SendOptions? options = null, CancellationToken cancellationToken = default) where T : class
        => SendBatchCoreAsync(MessageDestinationKind.Queue, messages, null, options, options?.Queue, cancellationToken);

    public Task<IReadOnlyList<string>> SendBatchAsync<T>(IEnumerable<T> messages, Func<T, MessageKeys> keys, SendOptions? options = null, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(keys);
        return SendBatchCoreAsync(MessageDestinationKind.Queue, messages, keys, options, options?.Queue, cancellationToken);
    }

    public Task<string> PublishAsync<T>(T message, PublishOptions? options = null, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(message);
        string id = options?.MessageId ?? NewMessageId();
        return SendCoreAsync(MessageDestinationKind.Topic, [message], [id], null, options, options?.Topic, isBatch: false, id, cancellationToken);
    }

    public Task<IReadOnlyList<string>> PublishBatchAsync<T>(IEnumerable<T> messages, PublishOptions? options = null, CancellationToken cancellationToken = default) where T : class
        => SendBatchCoreAsync(MessageDestinationKind.Topic, messages, null, options, options?.Topic, cancellationToken);

    public Task<IReadOnlyList<string>> PublishBatchAsync<T>(IEnumerable<T> messages, Func<T, MessageKeys> keys, PublishOptions? options = null, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(keys);
        return SendBatchCoreAsync(MessageDestinationKind.Topic, messages, keys, options, options?.Topic, cancellationToken);
    }

    public async Task<IAsyncDisposable> SubscribeAsync<T>(Func<T, IMessageContext, CancellationToken, Task> handler, SubscriptionOptions? options = null, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(handler);
        options ??= new SubscriptionOptions();
        options.Validate();
        options = options with { Scope = options.Scope ?? SubscriptionScope.Instance };

        var registration = MessageHandlerRegistration.ForDelegate(handler, MessageHandlerKind.Subscriber, options);
        registration.Resolve(EmptyServiceProvider.Instance, o => o, _runtime.Options.TestRetryDelay);
        _runtime.RegisterDerivedTypes(typeof(T));
        var processor = await _runtime.StartProcessorAsync(_runtime.GetSource(registration), [registration], retryInBackground: false, cancellationToken).AnyContext();
        return new AsyncDisposableAction(() => _runtime.StopProcessorAsync(processor));
    }

    private Task<IReadOnlyList<string>> SendBatchCoreAsync<T>(MessageDestinationKind kind, IEnumerable<T> messages, Func<T, MessageKeys>? keySelector,
        DeliveryOptions? options, string? destinationOverride, CancellationToken cancellationToken) where T : class
    {
        ArgumentNullException.ThrowIfNull(messages);
        var items = messages as IReadOnlyList<T> ?? messages.ToList();
        var ids = new string[items.Count];
        MessageKeys[]? keys = keySelector is null ? null : new MessageKeys[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            ArgumentNullException.ThrowIfNull(items[i], nameof(messages));
            if (keys is not null)
                keys[i] = keySelector!(items[i]);

            ids[i] = keys?[i].MessageId ?? NewMessageId();
        }

        return SendCoreAsync<T, IReadOnlyList<string>>(kind, items, ids, keys, options, destinationOverride, isBatch: true, ids, cancellationToken);
    }

    /// <summary>Sends <paramref name="items"/> with the matching <paramref name="ids"/> and <paramref name="keys"/>, and returns <paramref name="result"/>.</summary>
    private async Task<TResult> SendCoreAsync<T, TResult>(MessageDestinationKind kind, IReadOnlyList<T> items, string[] ids, MessageKeys[]? keys, DeliveryOptions? options,
        string? destinationOverride, bool isBatch, TResult result, CancellationToken cancellationToken) where T : class
    {
        if (items.Count == 0)
            return result;

        if (isBatch && (options?.MessageId is not null || options?.DeduplicationId is not null))
            throw new ArgumentException("MessageId and DeduplicationId identify a single message and cannot be used for a batch; pass a MessageKeys selector to give each message its own.", nameof(options));

        ValidateIdLength(options?.MessageId, nameof(DeliveryOptions.MessageId));
        ValidateIdLength(options?.GroupKey, nameof(DeliveryOptions.GroupKey));
        ValidateIdLength(options?.DeduplicationId, nameof(DeliveryOptions.DeduplicationId));
        bool anyGroupKey = options?.GroupKey is not null;
        bool anyDeduplicationId = options?.DeduplicationId is not null;
        if (keys is not null)
        {
            foreach (var key in keys)
            {
                ValidateIdLength(key.MessageId, nameof(MessageKeys.MessageId));
                ValidateIdLength(key.GroupKey, nameof(MessageKeys.GroupKey));
                ValidateIdLength(key.DeduplicationId, nameof(MessageKeys.DeduplicationId));
                anyGroupKey |= key.GroupKey is not null;
                anyDeduplicationId |= key.DeduplicationId is not null;
            }
        }

        var capabilities = _runtime.Transport.Capabilities;
        if (anyGroupKey || anyDeduplicationId)
            CheckUnsupportedOptions(anyGroupKey, anyDeduplicationId, capabilities, items);

        var now = _runtime.TimeProvider.GetUtcNow();
        var deliverAt = options?.DeliverAt ?? (options?.Delay is { } delay ? now.Add(delay) : null);
        if (deliverAt <= now)
            deliverAt = null;

        DateTimeOffset? nativeDeliverAt = deliverAt;
        var maxDelay = kind == MessageDestinationKind.Topic ? capabilities.MaxPublishDelay : capabilities.MaxDelay;
        if (deliverAt is { } requested && maxDelay is { } limit && requested - now > limit)
        {
            if (kind == MessageDestinationKind.Topic)
            {
                throw new MessageBusException(limit == TimeSpan.Zero
                    ? "This transport does not support delayed publishes. Send a command instead, or schedule a job."
                    : $"This transport supports publish delays up to {limit}. Send a command instead, or schedule a job.");
            }

            if (limit == TimeSpan.Zero)
                throw new MessageBusException("This transport does not support delayed delivery.");

            // Longer delays hop: the message is delivered at the limit and re-sent until it is due.
            nativeDeliverAt = now.Add(limit);
        }

        string? correlationId = options?.CorrelationId ?? MessageContext.Current?.CorrelationId;
        var headerValues = new SendHeaders(
            now.ToString("O", CultureInfo.InvariantCulture),
            options?.TimeToLive is { } ttl ? now.Add(ttl).ToString("O", CultureInfo.InvariantCulture) : null,
            deliverAt is { } requestedAt && nativeDeliverAt != deliverAt ? requestedAt.ToString("O", CultureInfo.InvariantCulture) : null,
            correlationId);
        var batch = new SendBatch<T>(items, ids, keys, options, nativeDeliverAt, headerValues);

        // One message, the usual case, goes to its route's destination without grouping the batch by destination.
        if (items.Count == 1)
        {
            await SendGroupAsync(kind, GetDestinationName(kind, items[0], destinationOverride), batch, FirstIndex, capabilities, cancellationToken).AnyContext();
            return result;
        }

        var groups = Enumerable.Range(0, items.Count).GroupBy(i => GetDestinationName(kind, items[i], destinationOverride));
        foreach (var group in groups)
            await SendGroupAsync(kind, group.Key, batch, group.ToList(), capabilities, cancellationToken).AnyContext();

        return result;
    }

    private string GetDestinationName<T>(MessageDestinationKind kind, T message, string? destinationOverride) where T : class
    {
        if (destinationOverride is not null)
            return MessageTypeRegistry.ValidateName(destinationOverride, "options");

        var route = _runtime.Types.GetRoute(message.GetType());
        return kind == MessageDestinationKind.Queue ? route.Queue : route.Topic;
    }

    /// <summary>
    /// Sends the messages of <paramref name="batch"/> at <paramref name="indices"/>, which share a destination. Messages of
    /// the same type in one call share their headers, which are the same for each and which transports only read.
    /// </summary>
    private async Task SendGroupAsync<T>(MessageDestinationKind kind, string destinationName, SendBatch<T> batch, IReadOnlyList<int> indices,
        TransportCapabilities capabilities, CancellationToken cancellationToken) where T : class
    {
        var destination = kind == MessageDestinationKind.Queue ? MessageDestination.Queue(destinationName) : MessageDestination.Topic(destinationName);
        var source = FoundatioDiagnostics.ActivitySource;
        using var activity = source.HasListeners() ? source.StartActivity($"{(kind == MessageDestinationKind.Queue ? "send" : "publish")} {destinationName}", ActivityKind.Producer) : null;
        var current = activity ?? Activity.Current;
        string? traceParent = current?.Id;
        string? traceState = current?.TraceStateString;

        var options = batch.Options;
        var outgoing = new List<OutgoingMessage>(indices.Count);
        MessageRoute? headersRoute = null;
        IReadOnlyDictionary<string, string>? headers = null;
        foreach (int index in indices)
        {
            var message = batch.Items[index];
            var route = _runtime.Types.GetRoute(message.GetType());
            if (!ReferenceEquals(route, headersRoute))
            {
                CheckRouting(kind, route, destinationName);
                headers = CreateHeaders(route, options?.Headers, batch.HeaderValues, traceParent, traceState);
                headersRoute = route;
            }

            byte[] body = _runtime.Serializer.SerializeToBytes<object>(message);
            string messageId = batch.Ids[index];
            string? groupKey = batch.Keys?[index].GroupKey ?? options?.GroupKey;
            string? deduplicationId = batch.Keys?[index].DeduplicationId ?? options?.DeduplicationId;
            if (capabilities.MaxMessageSize is { } maxSize)
            {
                long size = MessageSize.Of(messageId, body, headers!, groupKey, deduplicationId);
                if (size > maxSize)
                    throw new MessageTooLargeException($"Message {route.Name} is {size} bytes with its headers and ids, which exceeds the transport limit of {maxSize} bytes.") { Size = size, Limit = maxSize };
            }

            outgoing.Add(new OutgoingMessage
            {
                MessageId = messageId,
                Body = body,
                Headers = headers!,
                DeliverAt = batch.NativeDeliverAt,
                GroupKey = groupKey,
                DeduplicationId = deduplicationId
            });
        }

        int batchSize = Math.Max(1, capabilities.MaxSendBatchSize);
        if (outgoing.Count <= batchSize)
        {
            await _runtime.Transport.SendAsync(destination, outgoing, cancellationToken).AnyContext();
        }
        else
        {
            for (int i = 0; i < outgoing.Count; i += batchSize)
                await _runtime.Transport.SendAsync(destination, outgoing.GetRange(i, Math.Min(batchSize, outgoing.Count - i)), cancellationToken).AnyContext();
        }

        if (MessagingInstruments.Sent.Enabled)
            MessagingInstruments.Sent.Add(outgoing.Count, new KeyValuePair<string, object?>("messaging.destination", destination.ToString()));

        if (_runtime.Observer is { } observer)
        {
            for (int i = 0; i < indices.Count; i++)
                observer.OnSent(destination, batch.Items[indices[i]], outgoing[i]);
        }
    }

    /// <summary>The messages of one send and what they share, so each destination's group can pick its messages by index.</summary>
    private readonly record struct SendBatch<T>(IReadOnlyList<T> Items, string[] Ids, MessageKeys[]? Keys, DeliveryOptions? Options, DateTimeOffset? NativeDeliverAt, SendHeaders HeaderValues);

    private static string NewMessageId() => Guid.NewGuid().ToString("N");

    private Dictionary<string, string> CreateHeaders(MessageRoute route, IReadOnlyDictionary<string, string>? applicationHeaders, SendHeaders values, string? traceParent, string? traceState)
    {
        // Room for the application's headers and every header Foundatio adds, so the dictionary never grows.
        var headers = new Dictionary<string, string>((applicationHeaders?.Count ?? 0) + 8);
        if (applicationHeaders is not null)
        {
            foreach (var header in applicationHeaders)
                headers[header.Key] = header.Value;
        }

        headers[KnownHeaders.MessageType] = route.Name;
        if (_runtime.Types.GetBaseTypeNames(route.Type) is { } baseTypes)
            headers[KnownHeaders.MessageBaseTypes] = baseTypes;
        headers[KnownHeaders.SentAt] = values.SentAt;
        if (values.CorrelationId is not null)
            headers[KnownHeaders.CorrelationId] = values.CorrelationId;
        if (values.ExpiresAt is not null)
            headers[KnownHeaders.ExpiresAt] = values.ExpiresAt;
        if (values.DeliverAt is not null)
            headers[KnownHeaders.DeliverAt] = values.DeliverAt;
        if (traceParent is not null)
            headers[KnownHeaders.TraceParent] = traceParent;
        if (!String.IsNullOrEmpty(traceState))
            headers[KnownHeaders.TraceState] = traceState;

        return headers;
    }

    /// <summary>Header values that are the same for every message of one send, formatted once.</summary>
    private readonly record struct SendHeaders(string SentAt, string? ExpiresAt, string? DeliverAt, string? CorrelationId);

    /// <summary>
    /// Warns, once per message type, or with <see cref="MessagingOptions.RequireTransportCapabilities"/> throws, when a
    /// group key or deduplication id would not do on this transport what it does on one that supports it.
    /// </summary>
    private void CheckUnsupportedOptions<T>(bool groupKey, bool deduplicationId, TransportCapabilities capabilities, IReadOnlyCollection<T> messages) where T : class
    {
        bool unorderedGroup = groupKey && !capabilities.OrderedGroups;
        bool ignoredDeduplicationId = deduplicationId && !capabilities.Deduplication;
        if (!unorderedGroup && !ignoredDeduplicationId)
            return;

        string transport = _runtime.Transport.GetType().Name;
        foreach (var type in messages.Where(m => m is not null).Select(m => m.GetType()).Distinct())
        {
            string messageType = _runtime.Types.GetRoute(type).Name;
            if (unorderedGroup)
            {
                if (_runtime.Options.RequireTransportCapabilities)
                {
                    throw new MessageBusException($"{messageType} was sent with a GroupKey, but {transport} does not keep groups in order: messages of one group " +
                        "could be handled concurrently and out of order. Remove the GroupKey, or turn off MessagingOptions.RequireTransportCapabilities to send it anyway.");
                }

                if (ShouldReport("group", messageType))
                {
                    _logger.LogWarning("{MessageType} was sent with a GroupKey, but {Transport} does not keep groups in order: messages of one group may be handled " +
                        "concurrently and out of order. Set MessagingOptions.RequireTransportCapabilities to make this an error", messageType, transport);
                }
            }

            if (ignoredDeduplicationId)
            {
                if (_runtime.Options.RequireTransportCapabilities)
                {
                    throw new MessageBusException($"{messageType} was sent with a DeduplicationId, but {transport} does not deduplicate: a repeated send would be " +
                        "delivered again. Remove the DeduplicationId, or turn off MessagingOptions.RequireTransportCapabilities to send it anyway.");
                }

                if (ShouldReport("deduplication", messageType))
                {
                    _logger.LogWarning("{MessageType} was sent with a DeduplicationId, but {Transport} does not deduplicate: repeated sends are all delivered, " +
                        "so its handlers must be idempotent. Set MessagingOptions.RequireTransportCapabilities to make this an error", messageType, transport);
                }
            }
        }
    }

    /// <summary>
    /// Warns, once per message type, when a command is sent that this app only subscribes to, or an event is published
    /// that this app only consumes: the most common mix-up of <c>SendAsync</c> and <c>PublishAsync</c>. A sent message
    /// waits in a queue nothing here reads, and a published one is dropped when its topic has no subscriptions. Only
    /// this app's own handlers are known, so a mix-up between services is not detected.
    /// </summary>
    private void CheckRouting(MessageDestinationKind kind, MessageRoute route, string destinationName)
    {
        var checkedTypes = kind == MessageDestinationKind.Queue ? _checkedSends : _checkedPublishes;
        if (_handlers.Count == 0 || checkedTypes.ContainsKey(route.Type) || checkedTypes.Count >= MaxReportedMessageTypes || !checkedTypes.TryAdd(route.Type, 0))
            return;

        WarnIfMisrouted(kind, route, destinationName);
    }

    /// <summary>The first send of a type: kept apart from <see cref="CheckRouting"/> so its lambdas allocate nothing on every later send.</summary>
    private void WarnIfMisrouted(MessageDestinationKind kind, MessageRoute route, string destinationName)
    {
        if (kind == MessageDestinationKind.Queue)
        {
            if (_handlers.Any(h => h.Kind == MessageHandlerKind.Consumer && _runtime.Types.GetRoute(h.MessageType).Queue == destinationName)
                || !_handlers.Any(h => h.Kind == MessageHandlerKind.Subscriber && h.MessageType.IsAssignableFrom(route.Type)))
            {
                return;
            }

            _logger.LogWarning("{MessageType} was sent with SendAsync to queue {Queue}, which nothing in this app consumes, but this app subscribes to it with AddSubscriber. "
                + "Subscribers only receive published messages: publish events with PublishAsync, or consume the command with AddConsumer", route.Name, destinationName);
            return;
        }

        if (_handlers.Any(h => h.Kind == MessageHandlerKind.Subscriber && _runtime.Types.GetRoute(h.MessageType).Topic == destinationName)
            || _runtime.GetProcessors().Any(p => p.Source.Topic == destinationName)
            || !_handlers.Any(h => h.Kind == MessageHandlerKind.Consumer && h.MessageType.IsAssignableFrom(route.Type)))
        {
            return;
        }

        _logger.LogWarning("{MessageType} was published with PublishAsync to topic {Topic}, which nothing in this app subscribes to, but this app consumes it with AddConsumer. "
            + "Consumers only receive sent messages, and a topic with no subscriptions drops what is published to it: send commands with SendAsync, or subscribe "
            + "to the event with AddSubscriber", route.Name, destinationName);
    }

    private bool ShouldReport(string option, string messageType)
        => _reportedUnsupportedOptions.Count < MaxReportedMessageTypes && _reportedUnsupportedOptions.TryAdd(String.Concat(option, "|", messageType), 0);

    private static void ValidateIdLength(string? value, string name)
    {
        if (value is not null && (value.Length == 0 || value.Length > OutgoingMessage.MaxIdLength))
            throw new ArgumentException($"{name} must be 1 to {OutgoingMessage.MaxIdLength} characters.", name);
    }
}
