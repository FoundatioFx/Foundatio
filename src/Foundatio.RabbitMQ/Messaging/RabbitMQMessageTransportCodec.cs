using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RabbitMQ.Client;

namespace Foundatio.Messaging.Transport;

/// <summary>
/// Maps messages to AMQP messages. The body is the message body, and application headers are AMQP headers with the
/// same names and UTF-8 string values, so other RabbitMQ tooling sees them. The transport's own headers start with
/// <c>x-foundatio-</c>; <see cref="FormatHeader"/> tells its messages from other producers'.
/// </summary>
/// <remarks>
/// <para>
/// Some names belong to others: the broker sets <c>x-death</c> and <c>x-delivery-count</c> and routes by <c>CC</c> and
/// <c>BCC</c>, and RabbitMQ.Client adds <c>x-dotnet-pub-seq-no</c> to every message it confirms and replaces
/// <c>traceparent</c>, <c>tracestate</c> and <c>baggage</c> while it is traced. An application header with such a name,
/// or any name starting with <c>x-</c>, travels as <see cref="AliasPrefix"/> plus its name, and a header the transport
/// did not write under such a name is ignored on the transport's own messages.
/// </para>
/// <para>
/// AMQP sends all of a message's properties in one frame, and RabbitMQ limits only the size of the body. So when the
/// application headers would take more than the header table limit, or a name would be longer than 255 bytes, they
/// move into an envelope at the start of the body instead.
/// </para>
/// </remarks>
internal static class RabbitMQMessageTransportCodec
{
    public const string ReservedHeaderPrefix = "x-foundatio-";
    public const string FormatHeader = ReservedHeaderPrefix + "format";
    public const string IdHeader = ReservedHeaderPrefix + "id";
    public const string GroupHeader = ReservedHeaderPrefix + "group";
    public const string DeliveriesHeader = ReservedHeaderPrefix + "deliveries";
    public const string AliasPrefix = ReservedHeaderPrefix + "h-";

    /// <summary>Set by quorum queues on a message that went back to the queue because its channel closed.</summary>
    public const string BrokerDeliveryCountHeader = "x-delivery-count";

    /// <summary>
    /// Room left in the largest body for an envelope: the framing of at least 100 headers and a dead-letter reason of
    /// <see cref="MaxReasonBytes"/>.
    /// </summary>
    public const int EncodingReserve = 16 * 1024;

    /// <summary>Dead-letter reasons are cut to this many UTF-8 bytes.</summary>
    public const int MaxReasonBytes = 4 * 1024;

    /// <summary>
    /// The most bytes of application headers kept in the AMQP header table. The properties frame also carries the
    /// transport's headers, the dead-letter reason, the client's confirmation header and the broker's <c>x-death</c>
    /// entries, which grow by one per delay level a message passes through.
    /// </summary>
    public const int MaxHeaderTableBytes = 64 * 1024;

    /// <summary>What the header table leaves of a frame for everything else in the properties frame.</summary>
    private const int FrameReserve = 32 * 1024;

    private const int MaxShortStringBytes = 255;

    /// <summary>Application headers are in the AMQP header table.</summary>
    private const int TableFormat = 1;

    /// <summary>Application headers are in an envelope at the start of the body.</summary>
    private const int EnvelopeFormat = 2;

    private static readonly HashSet<string> ForeignNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CC", "BCC", "traceparent", "tracestate", "baggage", "Request-Id", "Correlation-Context"
    };

    /// <summary>The application header bytes the AMQP header table may hold on a connection with <paramref name="frameMax"/>.</summary>
    public static int GetHeaderTableLimit(uint frameMax)
    {
        if (frameMax == 0)
            return MaxHeaderTableBytes;

        return (int)Math.Clamp((long)frameMax - FrameReserve, 0, MaxHeaderTableBytes);
    }

    public static (BasicProperties Properties, ReadOnlyMemory<byte> Body) Encode(string messageId, ReadOnlyMemory<byte> body, IReadOnlyDictionary<string, string> headers,
        string? groupKey, int previousDeliveries, DateTimeOffset enqueuedAt, int headerTableLimit)
    {
        var table = new Dictionary<string, object?>(headers.Count + 4, StringComparer.Ordinal);
        var properties = new BasicProperties
        {
            Persistent = true,
            Timestamp = new AmqpTimestamp(enqueuedAt.ToUnixTimeSeconds()),
            Headers = table
        };

        if (Encoding.UTF8.GetByteCount(messageId) <= MaxShortStringBytes)
            properties.MessageId = messageId;
        else
            table[IdHeader] = Encoding.UTF8.GetBytes(messageId);

        if (groupKey is not null)
            table[GroupHeader] = Encoding.UTF8.GetBytes(groupKey);
        if (previousDeliveries > 0)
            table[DeliveriesHeader] = previousDeliveries;

        if (FitsInHeaderTable(headers, headerTableLimit))
        {
            table[FormatHeader] = TableFormat;
            foreach (var (name, value) in headers)
                table[ToTableName(name)] = Encoding.UTF8.GetBytes(value);

            return (properties, body);
        }

        table[FormatHeader] = EnvelopeFormat;
        return (properties, WriteEnvelope(headers, body.Span));
    }

    /// <summary>
    /// Reads a delivery written by this codec, or by any other producer: its AMQP headers that do not start with
    /// <c>x-</c> become headers, and its body is kept as it is.
    /// </summary>
    public static Decoded Decode(IReadOnlyBasicProperties properties, ReadOnlyMemory<byte> body)
    {
        var table = properties.Headers;
        string messageId = GetString(table, IdHeader) ?? (String.IsNullOrEmpty(properties.MessageId) ? Guid.NewGuid().ToString("N") : properties.MessageId);
        string? groupKey = GetString(table, GroupHeader);
        int deliveryCount = (int)Math.Min(Int32.MaxValue, 1L + GetCount(table, DeliveriesHeader) + GetCount(table, BrokerDeliveryCountHeader));
        DateTimeOffset? enqueuedAt = properties.IsTimestampPresent() ? DateTimeOffset.FromUnixTimeSeconds(properties.Timestamp.UnixTime) : null;
        long format = GetCount(table, FormatHeader);

        if (format == EnvelopeFormat && TryReadEnvelope(body, out var envelopeHeaders, out var payload))
            return new Decoded(messageId, payload, envelopeHeaders, groupKey, deliveryCount, enqueuedAt);

        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        if (table is not null)
        {
            foreach (var (name, value) in table)
            {
                string? headerName = format == TableFormat ? FromTableName(name) : name.StartsWith("x-", StringComparison.OrdinalIgnoreCase) ? null : name;
                if (headerName is not null && ToHeaderValue(value) is { } text)
                    headers[headerName] = text;
            }
        }

        return new Decoded(messageId, body, headers, groupKey, deliveryCount, enqueuedAt);
    }

    /// <summary>The longest prefix of <paramref name="reason"/> that fits in <see cref="MaxReasonBytes"/> of UTF-8.</summary>
    public static string TruncateReason(string reason)
    {
        if (Encoding.UTF8.GetByteCount(reason) <= MaxReasonBytes)
            return reason;

        int bytes = 0;
        for (int i = 0; i < reason.Length; i++)
        {
            int length = Char.IsHighSurrogate(reason[i]) && i + 1 < reason.Length && Char.IsLowSurrogate(reason[i + 1]) ? 2 : 1;
            int size = Encoding.UTF8.GetByteCount(reason.AsSpan(i, length));
            if (bytes + size > MaxReasonBytes)
                return reason[..i];

            bytes += size;
            i += length - 1;
        }

        return reason;
    }

    /// <summary>Names that are the application's own in the AMQP header table: not empty, not starting with <c>x-</c>, and not one the broker or client sets or routes by.</summary>
    private static bool IsPlainName(string name) => name.Length > 0 && !name.StartsWith("x-", StringComparison.OrdinalIgnoreCase) && !ForeignNames.Contains(name);

    private static string ToTableName(string name) => IsPlainName(name) ? name : AliasPrefix + name;

    private static string? FromTableName(string name)
    {
        if (name.StartsWith(AliasPrefix, StringComparison.Ordinal))
            return name[AliasPrefix.Length..];

        return IsPlainName(name) ? name : null;
    }

    private static bool FitsInHeaderTable(IReadOnlyDictionary<string, string> headers, int limit)
    {
        long size = 0;
        foreach (var (name, value) in headers)
        {
            int nameBytes = Encoding.UTF8.GetByteCount(name) + (IsPlainName(name) ? 0 : AliasPrefix.Length);
            if (nameBytes > MaxShortStringBytes)
                return false;

            // A field: the name's length octet and bytes, a type octet, and a long string's 4-byte length and bytes.
            size += 1 + nameBytes + 1 + 4 + Encoding.UTF8.GetByteCount(value);
            if (size > limit)
                return false;
        }

        return true;
    }

    private static byte[] WriteEnvelope(IReadOnlyDictionary<string, string> headers, ReadOnlySpan<byte> body)
    {
        int size = VarIntSize(headers.Count);
        foreach (var (name, value) in headers)
        {
            int nameBytes = Encoding.UTF8.GetByteCount(name);
            int valueBytes = Encoding.UTF8.GetByteCount(value);
            size += VarIntSize(nameBytes) + nameBytes + VarIntSize(valueBytes) + valueBytes;
        }

        var buffer = new byte[size + body.Length];
        int offset = WriteVarInt(buffer, headers.Count);
        foreach (var (name, value) in headers)
        {
            offset += WriteString(buffer.AsSpan(offset), name);
            offset += WriteString(buffer.AsSpan(offset), value);
        }

        body.CopyTo(buffer.AsSpan(offset));
        return buffer;
    }

    private static bool TryReadEnvelope(ReadOnlyMemory<byte> body, out Dictionary<string, string> headers, out ReadOnlyMemory<byte> payload)
    {
        headers = new Dictionary<string, string>(StringComparer.Ordinal);
        payload = body;
        var span = body.Span;
        int offset = 0;
        if (!TryReadVarInt(span, ref offset, out int count))
            return false;

        for (int i = 0; i < count; i++)
        {
            if (!TryReadString(span, ref offset, out string name) || !TryReadString(span, ref offset, out string value))
                return false;

            headers[name] = value;
        }

        payload = body[offset..];
        return true;
    }

    private static int WriteString(Span<byte> destination, string value)
    {
        int offset = WriteVarInt(destination, Encoding.UTF8.GetByteCount(value));
        return offset + Encoding.UTF8.GetBytes(value, destination[offset..]);
    }

    private static bool TryReadString(ReadOnlySpan<byte> source, ref int offset, out string value)
    {
        value = String.Empty;
        if (!TryReadVarInt(source, ref offset, out int length) || length > source.Length - offset)
            return false;

        value = Encoding.UTF8.GetString(source.Slice(offset, length));
        offset += length;
        return true;
    }

    private static int VarIntSize(int value)
    {
        int size = 1;
        for (uint remaining = (uint)value >> 7; remaining != 0; remaining >>= 7)
            size++;

        return size;
    }

    private static int WriteVarInt(Span<byte> destination, int value)
    {
        int offset = 0;
        uint remaining = (uint)value;
        while (remaining >= 0x80)
        {
            destination[offset++] = (byte)(remaining | 0x80);
            remaining >>= 7;
        }

        destination[offset++] = (byte)remaining;
        return offset;
    }

    private static bool TryReadVarInt(ReadOnlySpan<byte> source, ref int offset, out int value)
    {
        value = 0;
        for (int shift = 0; shift < 35; shift += 7)
        {
            if (offset >= source.Length)
                return false;

            byte part = source[offset++];
            value |= (part & 0x7F) << shift;
            if ((part & 0x80) == 0)
                return value >= 0;
        }

        return false;
    }

    private static string? GetString(IDictionary<string, object?>? table, string name)
    {
        return table is not null && table.TryGetValue(name, out object? value) ? ToHeaderValue(value) : null;
    }

    private static long GetCount(IDictionary<string, object?>? table, string name)
    {
        if (table is null || !table.TryGetValue(name, out object? value))
            return 0;

        long count = value switch
        {
            int number => number,
            long number => number,
            short number => number,
            byte number => number,
            sbyte number => number,
            uint number => number,
            ushort number => number,
            byte[] bytes when Int64.TryParse(Encoding.UTF8.GetString(bytes), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) => parsed,
            string text when Int64.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) => parsed,
            _ => 0
        };

        return Math.Max(0, count);
    }

    private static string? ToHeaderValue(object? value) => value switch
    {
        byte[] bytes => Encoding.UTF8.GetString(bytes),
        string text => text,
        bool flag => flag ? "true" : "false",
        AmqpTimestamp timestamp => timestamp.UnixTime.ToString(CultureInfo.InvariantCulture),
        IFormattable formattable and not IEnumerable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => null
    };

    /// <summary>A delivery's message as it was sent, and the number of times it has been delivered.</summary>
    public sealed record Decoded(string MessageId, ReadOnlyMemory<byte> Body, IReadOnlyDictionary<string, string> Headers, string? GroupKey,
        int DeliveryCount, DateTimeOffset? EnqueuedAt);
}
