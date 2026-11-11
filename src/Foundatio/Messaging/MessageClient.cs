using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging.Transport;
using Foundatio.Serializer;
using Foundatio.Utility;

namespace Foundatio.Messaging;

internal sealed class MessageClient : IMessageClient
{
    private readonly MessagingRuntime _runtime;

    public MessageClient(MessagingRuntime runtime)
    {
        _runtime = runtime;
    }

    public Task SendAsync<T>(T message, SendOptions? options = null, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(message);
        return SendCoreAsync(MessageDestinationKind.Queue, [message], options, options?.Queue, isBatch: false, cancellationToken);
    }

    public Task SendBatchAsync<T>(IEnumerable<T> messages, SendOptions? options = null, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(messages);
        return SendCoreAsync(MessageDestinationKind.Queue, messages, options, options?.Queue, isBatch: true, cancellationToken);
    }

    public Task PublishAsync<T>(T message, PublishOptions? options = null, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(message);
        return SendCoreAsync(MessageDestinationKind.Topic, [message], options, options?.Topic, isBatch: false, cancellationToken);
    }

    public Task PublishBatchAsync<T>(IEnumerable<T> messages, PublishOptions? options = null, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(messages);
        return SendCoreAsync(MessageDestinationKind.Topic, messages, options, options?.Topic, isBatch: true, cancellationToken);
    }

    public async Task<IAsyncDisposable> SubscribeAsync<T>(Func<T, IMessageContext, CancellationToken, Task> handler, SubscriptionOptions? options = null, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(handler);
        options ??= new SubscriptionOptions();
        options.Validate();
        options = options with { Scope = options.Scope ?? SubscriptionScope.Instance };

        var registration = MessageHandlerRegistration.ForDelegate(handler, MessageHandlerKind.Subscriber, options);
        var processor = await _runtime.StartProcessorAsync(_runtime.GetSource(registration), [registration], retryInBackground: false, cancellationToken).AnyContext();
        return new AsyncDisposableAction(() => _runtime.StopProcessorAsync(processor));
    }

    private async Task SendCoreAsync<T>(MessageDestinationKind kind, IEnumerable<T> messages, DeliveryOptions? options, string? destinationOverride, bool isBatch, CancellationToken cancellationToken) where T : class
    {
        var items = messages as IReadOnlyCollection<T> ?? messages.ToList();
        if (items.Count == 0)
            return;

        if (isBatch && (options?.MessageId is not null || options?.DeduplicationId is not null))
            throw new ArgumentException("MessageId and DeduplicationId identify a single message and cannot be used for a batch.", nameof(options));

        var capabilities = _runtime.Transport.Capabilities;
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
        var groups = items.GroupBy(message =>
        {
            ArgumentNullException.ThrowIfNull(message, nameof(messages));
            var route = _runtime.Types.GetRoute(message.GetType());
            return destinationOverride is not null ? MessageTypeRegistry.ValidateName(destinationOverride, nameof(options))
                : kind == MessageDestinationKind.Queue ? route.Queue : route.Topic;
        });

        foreach (var group in groups)
        {
            var destination = kind == MessageDestinationKind.Queue ? MessageDestination.Queue(group.Key) : MessageDestination.Topic(group.Key);
            using var activity = FoundatioDiagnostics.ActivitySource.StartActivity($"{(kind == MessageDestinationKind.Queue ? "send" : "publish")} {group.Key}", ActivityKind.Producer);
            string? traceParent = activity?.Id ?? Activity.Current?.Id;
            string? traceState = activity?.TraceStateString ?? Activity.Current?.TraceStateString;

            var outgoing = new List<OutgoingMessage>();
            foreach (var message in group)
            {
                var route = _runtime.Types.GetRoute(message.GetType());
                var headers = options?.Headers is { } applicationHeaders ? new Dictionary<string, string>(applicationHeaders) : new Dictionary<string, string>();
                headers[KnownHeaders.MessageType] = route.Name;
                headers[KnownHeaders.SentAt] = now.ToString("O", CultureInfo.InvariantCulture);
                if (correlationId is not null)
                    headers[KnownHeaders.CorrelationId] = correlationId;
                if (options?.TimeToLive is { } ttl)
                    headers[KnownHeaders.ExpiresAt] = now.Add(ttl).ToString("O", CultureInfo.InvariantCulture);
                if (deliverAt is { } requestedAt && nativeDeliverAt != deliverAt)
                    headers[KnownHeaders.DeliverAt] = requestedAt.ToString("O", CultureInfo.InvariantCulture);
                if (traceParent is not null)
                    headers[KnownHeaders.TraceParent] = traceParent;
                if (!String.IsNullOrEmpty(traceState))
                    headers[KnownHeaders.TraceState] = traceState;

                byte[] body = _runtime.Serializer.SerializeToBytes<object>(message);
                if (capabilities.MaxMessageSize is { } maxSize)
                {
                    long size = body.Length + headers.Sum(h => Encoding.UTF8.GetByteCount(h.Key) + Encoding.UTF8.GetByteCount(h.Value));
                    if (size > maxSize)
                        throw new MessageTooLargeException($"Message {route.Name} is {size} bytes with headers, which exceeds the transport limit of {maxSize} bytes.") { Size = size, Limit = maxSize };
                }

                outgoing.Add(new OutgoingMessage
                {
                    MessageId = options?.MessageId ?? Guid.NewGuid().ToString("N"),
                    Body = body,
                    Headers = headers,
                    DeliverAt = nativeDeliverAt,
                    GroupKey = options?.GroupKey,
                    DeduplicationId = options?.DeduplicationId
                });
            }

            int batchSize = Math.Max(1, capabilities.MaxSendBatchSize);
            for (int i = 0; i < outgoing.Count; i += batchSize)
                await _runtime.Transport.SendAsync(destination, outgoing.GetRange(i, Math.Min(batchSize, outgoing.Count - i)), cancellationToken).AnyContext();

            MessagingInstruments.Sent.Add(outgoing.Count, new KeyValuePair<string, object?>("messaging.destination", destination.ToString()));
        }
    }
}
