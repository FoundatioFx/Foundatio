using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Unicode;
using Azure.Core.Amqp;
using Azure.Messaging.ServiceBus;

namespace Foundatio.Messaging.Transport;

/// <summary>
/// Maps Foundatio messages to Service Bus messages. The body is sent as-is and each header becomes an application
/// property, so other Service Bus clients can read both. The group key is the session id. The transport's own
/// properties start with <c>x-foundatio-</c>, which applications may not use for headers.
/// </summary>
/// <remarks>
/// Service Bus keeps application properties in a header section of at most 64 KiB. Headers that would take more than
/// <see cref="MaxApplicationPropertyBytes"/> there travel at the start of the body instead, with their length in
/// <see cref="HeadersProperty"/>, so any message within <see cref="TransportCapabilities.MaxMessageSize"/> is accepted.
/// </remarks>
internal static class AzureServiceBusMessageTransportCodec
{
    public const string ReservedHeaderPrefix = "x-foundatio-";

    /// <summary>The Foundatio message id, when the Service Bus message id is something else (a deduplication id).</summary>
    public const string MessageIdProperty = "x-foundatio-id";

    /// <summary>Marks a message without a group key on a session entity, which goes to one of the ungrouped sessions.</summary>
    public const string UngroupedProperty = "x-foundatio-ungrouped";

    /// <summary>The session ids of messages without a group key on a session entity, followed by a number below <see cref="UngroupedSessionCount"/>.</summary>
    public const string UngroupedSessionPrefix = "x-foundatio-ungrouped-";

    /// <summary>How many sessions ungrouped messages are spread over, so competing receivers can share them.</summary>
    public const int UngroupedSessionCount = 8;

    /// <summary>The one subscription a message sent to the topic is for; every subscription's rule matches it or its absence.</summary>
    public const string TargetProperty = "x-foundatio-to";

    /// <summary>Deliveries before this copy of a message, added to its own Service Bus delivery count.</summary>
    public const string DeliveryCountProperty = "x-foundatio-delivery-count";

    /// <summary>When a message held back by its session receiver is due, in Unix milliseconds.</summary>
    public const string DeliverAtProperty = "x-foundatio-deliver-at";

    /// <summary>The length of the encoded headers at the start of the body, when they did not fit in application properties.</summary>
    public const string HeadersProperty = "x-foundatio-headers";

    /// <summary>
    /// What the transport keeps of Service Bus's message size limit for AMQP framing (about 4 bytes per header), its own
    /// properties, the copy of a retried message and the dead-letter reason.
    /// </summary>
    public const int ReservedBytes = 4096;

    /// <summary>Dead-letter reasons are cut to this many UTF-8 bytes; Service Bus allows 4,096 characters.</summary>
    public const int MaxDeadLetterReasonBytes = 1024;

    /// <summary>Header bytes, including framing, kept in application properties; more move into the body.</summary>
    public const int MaxApplicationPropertyBytes = 48 * 1024;

    private const int PropertyFraming = 10;
    private const string NativeDeadLetterReason = "DeadLetterReason";
    private const string NativeDeadLetterDescription = "DeadLetterErrorDescription";

    public static ServiceBusMessage Encode(OutgoingMessage message, AzureServiceBusEncodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(message);
        ValidateHeaders(message.Headers);

        var serviceBusMessage = new ServiceBusMessage(EncodeBody(message, out int? headerLength));
        var properties = serviceBusMessage.ApplicationProperties;
        if (headerLength is { } length)
        {
            properties[HeadersProperty] = length;
        }
        else
        {
            foreach (var header in message.Headers)
                properties[header.Key] = header.Value;
        }

        serviceBusMessage.MessageId = options.Deduplication
            ? message.DeduplicationId is { } deduplicationId
                ? options.TargetSubscription is { } subscription ? AzureServiceBusMessageTransportNames.SubscriptionDeduplicationId(subscription, deduplicationId) : deduplicationId
                : Guid.NewGuid().ToString("N")
            : message.MessageId;
        if (serviceBusMessage.MessageId != message.MessageId)
            properties[MessageIdProperty] = message.MessageId;

        if (message.GroupKey is { } groupKey)
        {
            serviceBusMessage.SessionId = groupKey;
        }
        else if (options.Sessions)
        {
            // Ungrouped messages share a few sessions, which a receiver takes many messages from at a time, rather than
            // one session each, which Service Bus would hand out one accept at a time.
            serviceBusMessage.SessionId = UngroupedSessionPrefix + Random.Shared.Next(UngroupedSessionCount).ToString(CultureInfo.InvariantCulture);
            properties[UngroupedProperty] = true;
        }

        if (options.TargetSubscription is { } target)
            properties[TargetProperty] = target;

        if (message.DeliverAt is { } deliverAt && deliverAt > options.Now)
        {
            // A session holds back its later messages only while the earlier one is in it, and Service Bus adds a
            // scheduled message to its session when it is due. So a delayed message with a group key goes in at once,
            // and the receiver that gets its session holds it, and the session, until it is due.
            if (options.Sessions && message.GroupKey is not null)
                properties[DeliverAtProperty] = deliverAt.ToUnixTimeMilliseconds();
            else
                serviceBusMessage.ScheduledEnqueueTime = DateTimeOffset.UtcNow + (deliverAt - options.Now);
        }

        return serviceBusMessage;
    }

    /// <summary>
    /// A copy of a received message to schedule as its retry: the same body, properties and session, with the deliveries
    /// so far, sent to <paramref name="targetSubscription"/> of the topic when it came from a subscription. The copy
    /// always gets a new Service Bus id, carrying the message's own: an entity that detects duplicates remembers the
    /// original's, and would drop the copy, even when the transport does not deduplicate.
    /// </summary>
    public static ServiceBusMessage EncodeRetry(ServiceBusReceivedMessage message, int deliveryCount, DateTimeOffset scheduledAt, string? targetSubscription)
    {
        var copy = new ServiceBusMessage(message) { ScheduledEnqueueTime = scheduledAt, MessageId = Guid.NewGuid().ToString("N") };
        copy.ApplicationProperties[DeliveryCountProperty] = deliveryCount;
        copy.ApplicationProperties.TryAdd(MessageIdProperty, message.MessageId);
        if (targetSubscription is not null)
            copy.ApplicationProperties[TargetProperty] = targetSubscription;
        else
            copy.ApplicationProperties.Remove(TargetProperty);

        return copy;
    }

    public static AzureServiceBusDecodedMessage Decode(ServiceBusReceivedMessage message, bool deadLetter)
    {
        var properties = message.ApplicationProperties;
        var body = GetBody(message);
        Dictionary<string, string> headers;
        if (TryGetInt64(properties, HeadersProperty) is { } headerLength && headerLength >= 0 && headerLength <= body.Length
            && TryDecodeHeaders(body.Span[..(int)headerLength], out var encodedHeaders))
        {
            headers = encodedHeaders;
            body = body[(int)headerLength..];
        }
        else
        {
            headers = new Dictionary<string, string>(properties.Count, StringComparer.Ordinal);
            foreach (var property in properties)
            {
                if (property.Key.StartsWith(ReservedHeaderPrefix, StringComparison.Ordinal))
                    continue;
                if (deadLetter && property.Key is NativeDeadLetterReason or NativeDeadLetterDescription)
                    continue;

                headers[property.Key] = ToHeaderValue(property.Value);
            }
        }

        if (deadLetter && (message.DeadLetterReason is { Length: > 0 } || message.DeadLetterErrorDescription is { Length: > 0 }))
        {
            headers[KnownHeaders.DeadLetterReason] = String.IsNullOrEmpty(message.DeadLetterErrorDescription)
                ? message.DeadLetterReason!
                : String.IsNullOrEmpty(message.DeadLetterReason) ? message.DeadLetterErrorDescription : $"{message.DeadLetterReason}: {message.DeadLetterErrorDescription}";
        }

        long carried = TryGetInt64(properties, DeliveryCountProperty) ?? 0;
        return new AzureServiceBusDecodedMessage(
            properties.TryGetValue(MessageIdProperty, out object? id) && id is string { Length: > 0 } messageId ? messageId : message.MessageId ?? message.SequenceNumber.ToString(CultureInfo.InvariantCulture),
            body,
            headers,
            properties.ContainsKey(UngroupedProperty) || String.IsNullOrEmpty(message.SessionId) ? null : message.SessionId,
            (int)Math.Clamp(message.DeliveryCount + carried, 1, Int32.MaxValue));
    }

    /// <summary>Whether a message was sent without a group key to a session entity, so it is in no group.</summary>
    public static bool IsUngrouped(ServiceBusReceivedMessage message) => message.ApplicationProperties.ContainsKey(UngroupedProperty);

    /// <summary>When a message a session receiver holds back is due, or null for one that is due at once.</summary>
    public static DateTimeOffset? GetDeliverAt(ServiceBusReceivedMessage message) =>
        TryGetInt64(message.ApplicationProperties, DeliverAtProperty) is { } at ? DateTimeOffset.FromUnixTimeMilliseconds(at) : null;

    public static string TruncateUtf8(string value, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes)
            return value;

        var buffer = new byte[maxBytes];
        Utf8.FromUtf16(value, buffer, out int charsRead, out _, replaceInvalidSequences: true, isFinalBlock: false);
        return value[..charsRead];
    }

    private static void ValidateHeaders(IReadOnlyDictionary<string, string> headers)
    {
        foreach (string name in headers.Keys)
        {
            if (name.StartsWith(ReservedHeaderPrefix, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Header \"{name}\" uses the prefix \"{ReservedHeaderPrefix}\", which the Service Bus transport reserves for its own properties.", nameof(headers));
        }
    }

    private static BinaryData EncodeBody(OutgoingMessage message, out int? headerLength)
    {
        headerLength = null;
        long propertyBytes = 0;
        foreach (var header in message.Headers)
            propertyBytes += Encoding.UTF8.GetByteCount(header.Key) + Encoding.UTF8.GetByteCount(header.Value) + PropertyFraming;

        if (propertyBytes <= MaxApplicationPropertyBytes)
            return new BinaryData(message.Body);

        using var stream = new MemoryStream((int)Math.Min(propertyBytes + message.Body.Length, Int32.MaxValue));
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write7BitEncodedInt(message.Headers.Count);
            foreach (var header in message.Headers)
            {
                writer.Write(header.Key);
                writer.Write(header.Value);
            }
        }

        headerLength = (int)stream.Length;
        stream.Write(message.Body.Span);
        return new BinaryData(stream.GetBuffer().AsMemory(0, (int)stream.Length));
    }

    private static bool TryDecodeHeaders(ReadOnlySpan<byte> encoded, out Dictionary<string, string> headers)
    {
        headers = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var stream = new MemoryStream(encoded.ToArray(), writable: false);
            using var reader = new BinaryReader(stream, Encoding.UTF8);
            int count = reader.Read7BitEncodedInt();
            for (int i = 0; i < count; i++)
            {
                string name = reader.ReadString();
                headers[name] = reader.ReadString();
            }

            return stream.Position == stream.Length;
        }
        catch (Exception ex) when (ex is EndOfStreamException or FormatException or IOException)
        {
            return false;
        }
    }

    /// <summary>The body of a data message, or of a message another client sent with an AMQP value or sequence body.</summary>
    private static ReadOnlyMemory<byte> GetBody(ServiceBusReceivedMessage message)
    {
        var amqp = message.GetRawAmqpMessage();
        if (amqp.Body.BodyType == AmqpMessageBodyType.Data)
            return message.Body.ToMemory();

        if (amqp.Body.TryGetValue(out object? value))
        {
            return value switch
            {
                null => ReadOnlyMemory<byte>.Empty,
                byte[] bytes => bytes,
                string text => Encoding.UTF8.GetBytes(text),
                _ => Encoding.UTF8.GetBytes(Convert.ToString(value, CultureInfo.InvariantCulture) ?? String.Empty)
            };
        }

        return ReadOnlyMemory<byte>.Empty;
    }

    private static string ToHeaderValue(object? value) => value switch
    {
        null => String.Empty,
        string text => text,
        byte[] bytes => Convert.ToBase64String(bytes),
        DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
        bool flag => flag ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? String.Empty
    };

    private static long? TryGetInt64(IReadOnlyDictionary<string, object> properties, string name)
    {
        if (!properties.TryGetValue(name, out object? value))
            return null;

        return value switch
        {
            int number => number,
            long number => number,
            short number => number,
            uint number => number,
            string text when Int64.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long number) => number,
            _ => null
        };
    }
}

/// <summary>How <see cref="AzureServiceBusMessageTransportCodec.Encode"/> maps a message for its destination.</summary>
/// <param name="Now">The transport's clock, against which <see cref="OutgoingMessage.DeliverAt"/> becomes a delay.</param>
/// <param name="Sessions">Whether entities require sessions.</param>
/// <param name="Deduplication">Whether entities detect duplicates by message id.</param>
/// <param name="TargetSubscription">The one subscription of the topic the message is for.</param>
internal readonly record struct AzureServiceBusEncodeOptions(DateTimeOffset Now, bool Sessions, bool Deduplication, string? TargetSubscription);

internal sealed record AzureServiceBusDecodedMessage(string MessageId, ReadOnlyMemory<byte> Body, IReadOnlyDictionary<string, string> Headers, string? GroupKey, int DeliveryCount);
