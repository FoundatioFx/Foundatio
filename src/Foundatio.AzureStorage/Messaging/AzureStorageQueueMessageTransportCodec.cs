using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Unicode;

namespace Foundatio.Messaging.Transport;

/// <summary>
/// Writes a message as one Storage queue message: a binary envelope (a marker, the message id, the group key, the
/// headers and the body), base64-encoded, since a queue message is XML text and bodies may be binary. Storage accepts
/// 64 KiB of message text, so the envelope can be 48 KiB. A message that is not an envelope, such as one written by
/// another producer, is read as a body alone: base64-decoded when it is base64, as it is from the Storage SDK's default
/// encoding, Event Grid and Azure Functions, and as UTF-8 text otherwise, with the queue's own message id.
/// </summary>
internal static class AzureStorageQueueMessageTransportCodec
{
    /// <summary>The most message text Storage accepts.</summary>
    public const int MaxMessageTextLength = 64 * 1024;

    /// <summary>The largest envelope whose base64 text fits in <see cref="MaxMessageTextLength"/>.</summary>
    public const int MaxEnvelopeSize = MaxMessageTextLength / 4 * 3;

    /// <summary>The bytes the envelope keeps for its framing: length prefixes of 100 headers, and a 1,024-byte dead-letter reason.</summary>
    public const int ReservedFramingSize = 2048;

    /// <summary>How large a message may be, as <see cref="MessageSize"/> measures it.</summary>
    public const int MaxMessageSize = MaxEnvelopeSize - ReservedFramingSize;

    /// <summary>The part of a dead-letter reason kept even when the message is at the size limit.</summary>
    public const int MinDeadLetterReasonBytes = 1024;

    private const byte Marker1 = 0xF0;
    private const byte Marker2 = 0x0D;
    private const byte Version = 1;
    private const byte HasGroupKey = 1;
    private const int HeaderSize = 4;

    /// <summary>The base64 message text of <paramref name="message"/>, and the size of its envelope.</summary>
    public static (string Text, int Size) Encode(OutgoingMessage message) =>
        Encode(message.MessageId, message.Body.Span, message.Headers, message.GroupKey);

    /// <summary>
    /// The message text of a dead letter of <paramref name="message"/>: its id, body, headers and group key, with the
    /// reason in <see cref="KnownHeaders.DeadLetterReason"/>, shortened as needed to fit the size limit.
    /// </summary>
    public static string EncodeDeadLetter(IncomingMessage message, string reason)
    {
        var headers = new Dictionary<string, string>(message.Headers, StringComparer.Ordinal);
        headers.Remove(KnownHeaders.DeadLetterReason);
        int size = GetEnvelopeSize(message.MessageId, message.Body.Span, headers, message.GroupKey);
        int keyBytes = Encoding.UTF8.GetByteCount(KnownHeaders.DeadLetterReason);
        int room = MaxEnvelopeSize - size - GetVarIntSize(keyBytes) - keyBytes - GetVarIntSize(MaxEnvelopeSize);
        headers[KnownHeaders.DeadLetterReason] = TruncateUtf8(reason, Math.Max(room, MinDeadLetterReasonBytes));
        return Encode(message.MessageId, message.Body.Span, headers, message.GroupKey).Text;
    }

    /// <summary>
    /// Reads a queue message's text. Returns false for a message that is not an envelope, whose
    /// <paramref name="body"/> is then the decoded text (or the text itself when it is not base64).
    /// </summary>
    public static bool TryDecode(ReadOnlyMemory<byte> text, out ReadOnlyMemory<byte> body, [NotNullWhen(true)] out string? messageId,
        out IReadOnlyDictionary<string, string>? headers, out string? groupKey)
    {
        messageId = null;
        headers = null;
        groupKey = null;
        body = text;

        byte[] decoded = new byte[Base64.GetMaxDecodedFromUtf8Length(text.Length)];
        if (Base64.DecodeFromUtf8(text.Span, decoded, out _, out int written) != System.Buffers.OperationStatus.Done)
            return false;

        body = decoded.AsMemory(0, written);
        var span = decoded.AsSpan(0, written);
        if (span.Length < HeaderSize || span[0] != Marker1 || span[1] != Marker2 || span[2] != Version)
            return false;

        byte flags = span[3];
        int position = HeaderSize;
        if (!TryReadString(span, ref position, out string? id))
            return false;

        string? group = null;
        if ((flags & HasGroupKey) != 0 && !TryReadString(span, ref position, out group))
            return false;

        if (!TryReadVarInt(span, ref position, out int count) || count > span.Length)
            return false;

        var read = count == 0 ? null : new Dictionary<string, string>(count, StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            if (!TryReadString(span, ref position, out string? key) || !TryReadString(span, ref position, out string? value))
                return false;

            read![key] = value;
        }

        body = decoded.AsMemory(position, written - position);
        messageId = id;
        groupKey = group;
        headers = read;
        return true;
    }

    /// <summary>Shortens <paramref name="value"/> to at most <paramref name="maxBytes"/> UTF-8 bytes without splitting a character.</summary>
    public static string TruncateUtf8(string value, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes)
            return value;

        byte[] buffer = new byte[maxBytes];
        Utf8.FromUtf16(value, buffer, out int charsRead, out _, replaceInvalidSequences: true, isFinalBlock: false);
        return value[..charsRead];
    }

    private static (string Text, int Size) Encode(string messageId, ReadOnlySpan<byte> body, IReadOnlyDictionary<string, string> headers, string? groupKey)
    {
        int size = GetEnvelopeSize(messageId, body, headers, groupKey);
        if (size > MaxEnvelopeSize)
            return (String.Empty, size);

        byte[] envelope = new byte[size];
        envelope[0] = Marker1;
        envelope[1] = Marker2;
        envelope[2] = Version;
        envelope[3] = groupKey is null ? (byte)0 : HasGroupKey;
        int position = HeaderSize;
        WriteString(envelope, ref position, messageId);
        if (groupKey is not null)
            WriteString(envelope, ref position, groupKey);

        WriteVarInt(envelope, ref position, headers.Count);
        foreach (var (key, value) in headers)
        {
            WriteString(envelope, ref position, key);
            WriteString(envelope, ref position, value);
        }

        body.CopyTo(envelope.AsSpan(position));
        return (Convert.ToBase64String(envelope), size);
    }

    private static int GetEnvelopeSize(string messageId, ReadOnlySpan<byte> body, IReadOnlyDictionary<string, string> headers, string? groupKey)
    {
        long size = HeaderSize + GetStringSize(messageId) + GetVarIntSize(headers.Count) + body.Length;
        if (groupKey is not null)
            size += GetStringSize(groupKey);

        foreach (var (key, value) in headers)
            size += GetStringSize(key) + GetStringSize(value);

        return (int)Math.Min(size, Int32.MaxValue);
    }

    private static int GetStringSize(string value)
    {
        int bytes = Encoding.UTF8.GetByteCount(value);
        return GetVarIntSize(bytes) + bytes;
    }

    private static int GetVarIntSize(int value)
    {
        int size = 1;
        while ((value >>= 7) != 0)
            size++;

        return size;
    }

    private static void WriteString(byte[] buffer, ref int position, string value)
    {
        int bytes = Encoding.UTF8.GetByteCount(value);
        WriteVarInt(buffer, ref position, bytes);
        position += Encoding.UTF8.GetBytes(value, buffer.AsSpan(position));
    }

    private static void WriteVarInt(byte[] buffer, ref int position, int value)
    {
        uint remaining = (uint)value;
        while (remaining >= 0x80)
        {
            buffer[position++] = (byte)(remaining | 0x80);
            remaining >>= 7;
        }

        buffer[position++] = (byte)remaining;
    }

    private static bool TryReadString(ReadOnlySpan<byte> buffer, ref int position, [NotNullWhen(true)] out string? value)
    {
        value = null;
        if (!TryReadVarInt(buffer, ref position, out int length) || length > buffer.Length - position)
            return false;

        value = Encoding.UTF8.GetString(buffer.Slice(position, length));
        position += length;
        return true;
    }

    private static bool TryReadVarInt(ReadOnlySpan<byte> buffer, ref int position, out int value)
    {
        value = 0;
        for (int shift = 0; shift < 32; shift += 7)
        {
            if (position >= buffer.Length)
                return false;

            byte b = buffer[position++];
            value |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return value >= 0;
        }

        return false;
    }
}
