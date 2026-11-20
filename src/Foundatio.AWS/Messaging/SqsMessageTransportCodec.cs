using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using System.Text.Unicode;

namespace Foundatio.Messaging.Transport;

/// <summary>
/// Encodes a message as an SQS or SNS body plus one binary message attribute, the envelope, holding the message id, the
/// group key, the requested delivery time, every header and, for a body that cannot be sent as text, the body. A body
/// that is valid UTF-8 made of characters SQS accepts is sent as-is; any other body travels in the envelope behind a
/// one-character placeholder. SQS and SNS count a binary attribute by its raw bytes, so neither binary bodies nor
/// headers grow in transit: a message costs its body and header bytes plus a small, bounded overhead. A message
/// without the envelope was not sent by this transport: its body is used exactly as received and the SQS message id
/// is used.
/// </summary>
internal static class SqsMessageTransportCodec
{
    public const string AttributeName = "foundatio";

    /// <summary>What SQS and SNS accept for one message: the body plus every attribute's name, type and value.</summary>
    public const int MaxBrokerMessageSize = 256 * 1024;

    /// <summary>
    /// Room kept for what <see cref="MessageSize"/> does not count: the envelope's attribute name and type, the body
    /// placeholder, the delivery time, length prefixes for the id, group key and about 1,500 short headers, and the
    /// dead-letter reason header that <see cref="MaxDeadLetterReasonBytes"/> bounds, so a message at the limit can
    /// still be dead-lettered.
    /// </summary>
    public const int ReservedSize = 4 * 1024;

    /// <summary>The declared limit, measured by <see cref="MessageSize.Of(OutgoingMessage)"/>.</summary>
    public const int MaxMessageSize = MaxBrokerMessageSize - ReservedSize;

    public const int MaxDeadLetterReasonBytes = 1024;

    private const string AttributeDataType = "Binary";
    private const byte Version = 1;
    private const string BodyPlaceholder = "-";
    private static readonly IReadOnlyDictionary<string, string> EmptyHeaders = new Dictionary<string, string>();

    public static SqsEncodedMessage Encode(OutgoingMessage message, DateTimeOffset? deliverAt)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrEmpty(message.MessageId, nameof(message));

        var bodyBytes = message.Body.Span;
        var flags = EnvelopeFlags.None;
        string body;
        if (bodyBytes.IsEmpty)
        {
            flags |= EnvelopeFlags.EmptyBody;
            body = BodyPlaceholder;
        }
        else if (TryGetText(bodyBytes) is { } text)
        {
            body = text;
        }
        else
        {
            flags |= EnvelopeFlags.BodyInEnvelope;
            body = BodyPlaceholder;
        }

        if (message.GroupKey is not null)
            flags |= EnvelopeFlags.GroupKey;
        if (deliverAt is not null)
            flags |= EnvelopeFlags.DeliverAt;

        int size = 2 + StringSize(message.MessageId) + VarIntSize(message.Headers.Count);
        if (message.GroupKey is not null)
            size += StringSize(message.GroupKey);
        if (deliverAt is not null)
            size += sizeof(long);
        foreach (var (name, value) in message.Headers)
            size += StringSize(name) + StringSize(value);
        if (flags.HasFlag(EnvelopeFlags.BodyInEnvelope))
            size += bodyBytes.Length;

        var envelope = new byte[size];
        var writer = new Writer(envelope);
        writer.WriteByte(Version);
        writer.WriteByte((byte)flags);
        writer.WriteString(message.MessageId);
        if (message.GroupKey is not null)
            writer.WriteString(message.GroupKey);
        if (deliverAt is { } at)
            writer.WriteInt64(at.ToUnixTimeMilliseconds());
        writer.WriteVarInt(message.Headers.Count);
        foreach (var (name, value) in message.Headers)
        {
            writer.WriteString(name);
            writer.WriteString(value);
        }

        if (flags.HasFlag(EnvelopeFlags.BodyInEnvelope))
            writer.WriteBytes(bodyBytes);

        return new SqsEncodedMessage(message.MessageId, body, envelope, GetBrokerSize(body, envelope));
    }

    /// <summary>A marker a purge sends to learn the queue's own clock; receivers delete it on sight.</summary>
    public static SqsEncodedMessage EncodePurgeMarker(string markerId)
    {
        var envelope = new byte[2 + StringSize(markerId) + VarIntSize(0)];
        var writer = new Writer(envelope);
        writer.WriteByte(Version);
        writer.WriteByte((byte)(EnvelopeFlags.PurgeMarker | EnvelopeFlags.EmptyBody));
        writer.WriteString(markerId);
        writer.WriteVarInt(0);
        return new SqsEncodedMessage(markerId, BodyPlaceholder, envelope, GetBrokerSize(BodyPlaceholder, envelope));
    }

    public static SqsDecodedMessage Decode(string? body, Amazon.SQS.Model.MessageAttributeValue? attribute, string fallbackMessageId)
    {
        body ??= String.Empty;
        if (attribute is not { DataType: AttributeDataType, BinaryValue: { } value } || !TryReadEnvelope(GetBytes(value), body, out var decoded))
            return new SqsDecodedMessage(fallbackMessageId, Encoding.UTF8.GetBytes(body), EmptyHeaders, null, null, false);

        return decoded with { MessageId = decoded.MessageId.Length > 0 ? decoded.MessageId : fallbackMessageId };
    }

    public static Dictionary<string, Amazon.SQS.Model.MessageAttributeValue> ToSqsAttributes(SqsEncodedMessage message) => new()
    {
        [AttributeName] = new Amazon.SQS.Model.MessageAttributeValue { DataType = AttributeDataType, BinaryValue = new MemoryStream(message.Envelope, writable: false) }
    };

    public static Dictionary<string, Amazon.SimpleNotificationService.Model.MessageAttributeValue> ToSnsAttributes(SqsEncodedMessage message) => new()
    {
        [AttributeName] = new Amazon.SimpleNotificationService.Model.MessageAttributeValue { DataType = AttributeDataType, BinaryValue = new MemoryStream(message.Envelope, writable: false) }
    };

    /// <summary>Shortens <paramref name="value"/> to at most <paramref name="maxBytes"/> UTF-8 bytes without splitting a character.</summary>
    public static string TruncateUtf8(string value, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes)
            return value;

        Span<byte> buffer = stackalloc byte[maxBytes];
        Utf8.FromUtf16(value, buffer, out int charsRead, out _, replaceInvalidSequences: true, isFinalBlock: false);
        return value[..charsRead];
    }

    /// <summary>SQS and SNS count the body's UTF-8 bytes plus the attribute's name, data type and raw value bytes.</summary>
    private static int GetBrokerSize(string body, byte[] envelope) =>
        Encoding.UTF8.GetByteCount(body) + AttributeName.Length + AttributeDataType.Length + envelope.Length;

    private static ReadOnlySpan<byte> GetBytes(MemoryStream stream) =>
        stream.TryGetBuffer(out var buffer) ? buffer.AsSpan() : stream.ToArray();

    private static bool TryReadEnvelope(ReadOnlySpan<byte> envelope, string body, [NotNullWhen(true)] out SqsDecodedMessage? decoded)
    {
        decoded = null;
        var reader = new Reader(envelope);
        if (!reader.TryReadByte(out byte version) || version != Version || !reader.TryReadByte(out byte flagByte) || !reader.TryReadString(out string? id))
            return false;

        var flags = (EnvelopeFlags)flagByte;
        string? groupKey = null;
        if (flags.HasFlag(EnvelopeFlags.GroupKey) && !reader.TryReadString(out groupKey))
            return false;

        DateTimeOffset? deliverAt = null;
        if (flags.HasFlag(EnvelopeFlags.DeliverAt))
        {
            if (!reader.TryReadInt64(out long milliseconds) || milliseconds < -62135596800000 || milliseconds > 253402300799999)
                return false;

            deliverAt = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        }

        if (!reader.TryReadVarInt(out int headerCount))
            return false;

        var headers = new Dictionary<string, string>(Math.Min(headerCount, 64), StringComparer.Ordinal);
        for (int i = 0; i < headerCount; i++)
        {
            if (!reader.TryReadString(out string? name) || !reader.TryReadString(out string? value))
                return false;

            headers[name] = value;
        }

        byte[] bytes;
        if (flags.HasFlag(EnvelopeFlags.EmptyBody))
            bytes = [];
        else if (flags.HasFlag(EnvelopeFlags.BodyInEnvelope))
            bytes = reader.Remaining.ToArray();
        else
            bytes = Encoding.UTF8.GetBytes(body);

        decoded = new SqsDecodedMessage(id, bytes, headers, groupKey, deliverAt, flags.HasFlag(EnvelopeFlags.PurgeMarker));
        return true;
    }

    private static string? TryGetText(ReadOnlySpan<byte> bytes)
    {
        if (!Utf8.IsValid(bytes))
            return null;

        // SNS rejects a message that is only whitespace.
        string text = Encoding.UTF8.GetString(bytes);
        if (String.IsNullOrWhiteSpace(text))
            return null;

        foreach (char c in text)
        {
            // SQS accepts #x9, #xA, #xD, #x20-#xD7FF, #xE000-#xFFFD and #x10000-#x10FFFF (paired surrogates in valid UTF-8).
            if ((c < ' ' && c is not ('\t' or '\n' or '\r')) || c is '\uFFFE' or '\uFFFF')
                return null;
        }

        return text;
    }

    private static int StringSize(string value)
    {
        int length = Encoding.UTF8.GetByteCount(value);
        return VarIntSize(length) + length;
    }

    private static int VarIntSize(int value)
    {
        int size = 1;
        for (uint remaining = (uint)value >> 7; remaining != 0; remaining >>= 7)
            size++;

        return size;
    }

    [Flags]
    private enum EnvelopeFlags : byte
    {
        None = 0,
        BodyInEnvelope = 1,
        EmptyBody = 2,
        GroupKey = 4,
        DeliverAt = 8,
        PurgeMarker = 16
    }

    private ref struct Writer(Span<byte> buffer)
    {
        private readonly Span<byte> _buffer = buffer;
        private int _position;

        public void WriteByte(byte value) => _buffer[_position++] = value;

        public void WriteVarInt(int value)
        {
            uint remaining = (uint)value;
            while (remaining >= 0x80)
            {
                _buffer[_position++] = (byte)(remaining | 0x80);
                remaining >>= 7;
            }

            _buffer[_position++] = (byte)remaining;
        }

        public void WriteInt64(long value)
        {
            BinaryPrimitives.WriteInt64LittleEndian(_buffer[_position..], value);
            _position += sizeof(long);
        }

        public void WriteString(string value)
        {
            WriteVarInt(Encoding.UTF8.GetByteCount(value));
            _position += Encoding.UTF8.GetBytes(value, _buffer[_position..]);
        }

        public void WriteBytes(ReadOnlySpan<byte> value)
        {
            value.CopyTo(_buffer[_position..]);
            _position += value.Length;
        }
    }

    private ref struct Reader(ReadOnlySpan<byte> buffer)
    {
        private readonly ReadOnlySpan<byte> _buffer = buffer;
        private int _position;

        public readonly ReadOnlySpan<byte> Remaining => _buffer[_position..];

        public bool TryReadByte(out byte value)
        {
            value = 0;
            if (_position >= _buffer.Length)
                return false;

            value = _buffer[_position++];
            return true;
        }

        public bool TryReadVarInt(out int value)
        {
            value = 0;
            uint result = 0;
            for (int shift = 0; shift < 35; shift += 7)
            {
                if (!TryReadByte(out byte next))
                    return false;

                result |= (uint)(next & 0x7F) << shift;
                if ((next & 0x80) == 0)
                {
                    value = (int)result;
                    return value >= 0;
                }
            }

            return false;
        }

        public bool TryReadInt64(out long value)
        {
            value = 0;
            if (_buffer.Length - _position < sizeof(long))
                return false;

            value = BinaryPrimitives.ReadInt64LittleEndian(_buffer[_position..]);
            _position += sizeof(long);
            return true;
        }

        public bool TryReadString(out string value)
        {
            value = String.Empty;
            if (!TryReadVarInt(out int length) || length > _buffer.Length - _position)
                return false;

            value = Encoding.UTF8.GetString(_buffer.Slice(_position, length));
            _position += length;
            return true;
        }
    }
}

/// <summary>
/// A message ready to send: <see cref="Size"/> is what SQS and SNS count against their payload limit. FIFO queues and
/// topics also get <see cref="GroupId"/> and <see cref="DeduplicationId"/>.
/// </summary>
internal sealed record SqsEncodedMessage(string MessageId, string Body, byte[] Envelope, int Size)
{
    public int DelaySeconds { get; init; }
    public string? GroupId { get; init; }
    public string? DeduplicationId { get; init; }
}

internal sealed record SqsDecodedMessage(string MessageId, byte[] Body, IReadOnlyDictionary<string, string> Headers, string? GroupKey, DateTimeOffset? DeliverAt, bool IsPurgeMarker);
