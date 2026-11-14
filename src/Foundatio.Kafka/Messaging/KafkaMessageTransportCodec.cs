using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Confluent.Kafka;

namespace Foundatio.Messaging.Transport;

/// <summary>
/// Maps messages to Kafka records. The body is the record value and the group key is the record key, so a group
/// stays on one partition. Application headers are Kafka headers with the same names, so other Kafka tooling sees
/// <c>traceparent</c> and friends; the transport's own headers use a reserved prefix.
/// </summary>
internal static class KafkaMessageTransportCodec
{
    public const string ReservedHeaderPrefix = "x-foundatio-";
    public const string IdHeader = ReservedHeaderPrefix + "id";
    public const string DeliveryCountHeader = ReservedHeaderPrefix + "delivery-count";
    public const string EnqueuedAtHeader = ReservedHeaderPrefix + "enqueued-at";
    public const string DeliverAtHeader = ReservedHeaderPrefix + "deliver-at";
    public const string TargetHeader = ReservedHeaderPrefix + "target";

    /// <summary>Room left in every record for the transport's headers and Kafka's record framing.</summary>
    public const int EncodingReserve = 8 * 1024;

    /// <summary>Dead-letter reasons are cut to this many characters so a dead letter always fits in <see cref="EncodingReserve"/>.</summary>
    public const int MaxReasonLength = 1024;

    /// <summary>What a decoded record costs in memory beyond its bytes: the record, its strings and its header dictionary.</summary>
    private const int DecodedOverhead = 512;

    public static Message<byte[]?, byte[]> Encode(string messageId, ReadOnlyMemory<byte> body, IReadOnlyDictionary<string, string> headers,
        string? groupKey, int deliveryCount = 1, DateTimeOffset? enqueuedAt = null)
    {
        var kafkaHeaders = new Headers { { IdHeader, Encoding.UTF8.GetBytes(messageId) } };
        foreach (var (name, value) in headers)
            kafkaHeaders.Add(name, Encoding.UTF8.GetBytes(value));

        if (deliveryCount > 1)
            kafkaHeaders.Add(DeliveryCountHeader, Encoding.UTF8.GetBytes(deliveryCount.ToString(CultureInfo.InvariantCulture)));
        if (enqueuedAt is { } enqueued)
            kafkaHeaders.Add(EnqueuedAtHeader, Encoding.UTF8.GetBytes(enqueued.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)));

        return new Message<byte[]?, byte[]>
        {
            Key = groupKey is null ? null : Encoding.UTF8.GetBytes(groupKey),
            Value = ToArray(body),
            Headers = kafkaHeaders
        };
    }

    /// <summary>Routes a record through a delay tier: the forwarder sends it to <paramref name="target"/> at <paramref name="deliverAt"/>.</summary>
    public static Message<byte[]?, byte[]> WithDelay(Message<byte[]?, byte[]> message, string target, DateTimeOffset deliverAt)
    {
        message.Headers.Remove(TargetHeader);
        message.Headers.Remove(DeliverAtHeader);
        message.Headers.Add(TargetHeader, Encoding.UTF8.GetBytes(target));
        message.Headers.Add(DeliverAtHeader, Encoding.UTF8.GetBytes(deliverAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)));
        return message;
    }

    /// <summary>Marks a record its receiver holds until <paramref name="deliverAt"/>.</summary>
    public static Message<byte[]?, byte[]> WithDeliverAt(Message<byte[]?, byte[]> message, DateTimeOffset deliverAt)
    {
        message.Headers.Add(DeliverAtHeader, Encoding.UTF8.GetBytes(deliverAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)));
        return message;
    }

    /// <summary>The record a delay tier forwards to its target: the same record without the delay headers.</summary>
    public static Message<byte[]?, byte[]> WithoutDelay(ConsumeResult<byte[]?, byte[]?> result)
    {
        var headers = new Headers();
        foreach (var header in result.Message.Headers ?? [])
        {
            if (header.Key is not (TargetHeader or DeliverAtHeader))
                headers.Add(header.Key, header.GetValueBytes());
        }

        return new Message<byte[]?, byte[]> { Key = result.Message.Key, Value = result.Message.Value ?? [], Headers = headers };
    }

    /// <summary>A copy of a delay-tier record for another tier.</summary>
    public static Message<byte[]?, byte[]> Copy(ConsumeResult<byte[]?, byte[]?> result)
    {
        var headers = new Headers();
        foreach (var header in result.Message.Headers ?? [])
            headers.Add(header.Key, header.GetValueBytes());

        return new Message<byte[]?, byte[]> { Key = result.Message.Key, Value = result.Message.Value ?? [], Headers = headers };
    }

    public static KafkaRecord Decode(ConsumeResult<byte[]?, byte[]?> result)
    {
        var message = result.Message;
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        long size = DecodedOverhead + (message.Key?.Length ?? 0) + (message.Value?.Length ?? 0);
        string? messageId = null;
        int deliveryCount = 1;
        DateTimeOffset? enqueuedAt = null;
        DateTimeOffset? deliverAt = null;

        foreach (var header in message.Headers ?? [])
        {
            var bytes = header.GetValueBytes();
            size += header.Key.Length + (bytes?.Length ?? 0);
            string value = bytes is null ? String.Empty : Encoding.UTF8.GetString(bytes);
            switch (header.Key)
            {
                case IdHeader:
                    messageId = value;
                    break;
                case DeliveryCountHeader when Int32.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int count) && count > 0:
                    deliveryCount = count;
                    break;
                case EnqueuedAtHeader when Int64.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long milliseconds):
                    enqueuedAt = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
                    break;
                case DeliverAtHeader when Int64.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long milliseconds):
                    deliverAt = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
                    break;
                default:
                    if (!header.Key.StartsWith(ReservedHeaderPrefix, StringComparison.Ordinal))
                        headers[header.Key] = value;
                    break;
            }
        }

        return new KafkaRecord
        {
            MessageId = String.IsNullOrEmpty(messageId) ? $"{result.Topic}-{result.Partition.Value}-{result.Offset.Value}" : messageId,
            Body = message.Value ?? [],
            Headers = headers,
            GroupKey = message.Key is null ? null : Encoding.UTF8.GetString(message.Key),
            DeliveryCount = deliveryCount,
            EnqueuedAt = enqueuedAt ?? (message.Timestamp.Type == TimestampType.NotAvailable ? null : new DateTimeOffset(message.Timestamp.UtcDateTime)),
            Size = (int)Math.Min(size, Int32.MaxValue),
            AvailableAt = deliverAt
        };
    }

    public static string? GetHeader(ConsumeResult<byte[]?, byte[]?> result, string name)
    {
        return result.Message.Headers is { } headers && headers.TryGetLastBytes(name, out byte[] value) ? Encoding.UTF8.GetString(value) : null;
    }

    public static DateTimeOffset? GetTime(ConsumeResult<byte[]?, byte[]?> result, string name)
    {
        return Int64.TryParse(GetHeader(result, name), NumberStyles.None, CultureInfo.InvariantCulture, out long milliseconds)
            ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
            : null;
    }

    /// <summary>What librdkafka counts against <c>message.max.bytes</c>: value, key and serialized headers.</summary>
    public static long GetSize(Message<byte[]?, byte[]> message)
    {
        long size = message.Value.Length + (message.Key?.Length ?? 0);
        foreach (var header in message.Headers)
            size += Encoding.UTF8.GetByteCount(header.Key) + (header.GetValueBytes()?.Length ?? 0) + 10;

        return size;
    }

    public static void ThrowIfReserved(IReadOnlyDictionary<string, string> headers)
    {
        foreach (string name in headers.Keys)
        {
            if (name.StartsWith(ReservedHeaderPrefix, StringComparison.Ordinal))
                throw new ArgumentException($"Header \"{name}\" uses the prefix \"{ReservedHeaderPrefix}\", which the Kafka transport reserves.", nameof(headers));
        }
    }

    private static byte[] ToArray(ReadOnlyMemory<byte> body)
    {
        return MemoryMarshal.TryGetArray(body, out var segment) && segment.Array is { } array && segment.Offset == 0 && segment.Count == array.Length
            ? array
            : body.ToArray();
    }
}
