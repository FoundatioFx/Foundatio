using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Unicode;

namespace Foundatio.Messaging.Transport;

/// <summary>
/// Encodes a message as an SQS or SNS body plus one string message attribute holding a JSON envelope with the message
/// id, every header, the group key and, for topics, the requested delivery time. A body that is valid UTF-8 made of
/// characters SQS accepts is sent as-is; any other body is sent as base64, and an empty body as a placeholder, with the
/// encoding recorded in the envelope. A message without the envelope attribute was not sent by this transport: its
/// body is used exactly as received and the SQS message id is used.
/// </summary>
internal static class SqsMessageTransportCodec
{
    public const string AttributeName = "foundatio";
    private const string AttributeDataType = "String";
    private const string IdProperty = "id";
    private const string HeadersProperty = "h";
    private const string EncodingProperty = "e";
    private const string GroupKeyProperty = "g";
    private const string DeliverAtProperty = "at";
    private const string Base64Encoding = "base64";
    private const string EmptyEncoding = "empty";
    private const string EmptyBodyPlaceholder = "-";
    private static readonly IReadOnlyDictionary<string, string> EmptyHeaders = new Dictionary<string, string>();

    public static SqsEncodedMessage Encode(OutgoingMessage message, DateTimeOffset? deliverAt)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrEmpty(message.MessageId, nameof(message));

        var bodyBytes = message.Body.Span;
        string? encoding = null;
        string body;
        if (bodyBytes.IsEmpty)
        {
            encoding = EmptyEncoding;
            body = EmptyBodyPlaceholder;
        }
        else if (TryGetText(bodyBytes) is { } text)
        {
            body = text;
        }
        else
        {
            encoding = Base64Encoding;
            body = Convert.ToBase64String(bodyBytes);
        }

        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString(IdProperty, message.MessageId);
            if (message.Headers.Count > 0)
            {
                writer.WriteStartObject(HeadersProperty);
                foreach (var (name, value) in message.Headers)
                    writer.WriteString(name, value);
                writer.WriteEndObject();
            }

            if (encoding is not null)
                writer.WriteString(EncodingProperty, encoding);
            if (message.GroupKey is not null)
                writer.WriteString(GroupKeyProperty, message.GroupKey);
            if (deliverAt is { } at)
                writer.WriteNumber(DeliverAtProperty, at.ToUnixTimeMilliseconds());
            writer.WriteEndObject();
        }

        // The default JSON encoder escapes everything outside printable ASCII, so the attribute is always legal for SQS.
        string attribute = Encoding.ASCII.GetString(buffer.WrittenSpan);
        int size = Encoding.UTF8.GetByteCount(body) + AttributeName.Length + AttributeDataType.Length + attribute.Length;
        return new SqsEncodedMessage(message.MessageId, body, attribute, size);
    }

    public static SqsDecodedMessage Decode(string? body, string? attribute, string fallbackMessageId)
    {
        body ??= String.Empty;
        if (attribute is null || !TryReadEnvelope(attribute, out var envelope))
            return new SqsDecodedMessage(fallbackMessageId, Encoding.UTF8.GetBytes(body), EmptyHeaders, null, null);

        byte[] bytes = envelope.Encoding switch
        {
            EmptyEncoding => [],
            Base64Encoding => FromBase64(body),
            _ => Encoding.UTF8.GetBytes(body)
        };

        return new SqsDecodedMessage(envelope.Id ?? fallbackMessageId, bytes, envelope.Headers, envelope.GroupKey, envelope.DeliverAt);
    }

    public static Dictionary<string, Amazon.SQS.Model.MessageAttributeValue> ToSqsAttributes(SqsEncodedMessage message) => new()
    {
        [AttributeName] = new Amazon.SQS.Model.MessageAttributeValue { DataType = AttributeDataType, StringValue = message.Attribute }
    };

    public static Dictionary<string, Amazon.SimpleNotificationService.Model.MessageAttributeValue> ToSnsAttributes(SqsEncodedMessage message) => new()
    {
        [AttributeName] = new Amazon.SimpleNotificationService.Model.MessageAttributeValue { DataType = AttributeDataType, StringValue = message.Attribute }
    };

    private static byte[] FromBase64(string body)
    {
        try
        {
            return Convert.FromBase64String(body);
        }
        catch (FormatException)
        {
            return Encoding.UTF8.GetBytes(body);
        }
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

    private static bool TryReadEnvelope(string attribute, out Envelope envelope)
    {
        envelope = default;
        try
        {
            using var document = JsonDocument.Parse(attribute);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            string? id = null, encoding = null, groupKey = null;
            DateTimeOffset? deliverAt = null;
            var headers = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                switch (property.Name)
                {
                    case IdProperty when property.Value.ValueKind == JsonValueKind.String:
                        id = property.Value.GetString();
                        break;
                    case HeadersProperty when property.Value.ValueKind == JsonValueKind.Object:
                        foreach (var header in property.Value.EnumerateObject())
                        {
                            if (header.Value.ValueKind == JsonValueKind.String)
                                headers[header.Name] = header.Value.GetString()!;
                        }
                        break;
                    case EncodingProperty when property.Value.ValueKind == JsonValueKind.String:
                        encoding = property.Value.GetString();
                        break;
                    case GroupKeyProperty when property.Value.ValueKind == JsonValueKind.String:
                        groupKey = property.Value.GetString();
                        break;
                    case DeliverAtProperty when property.Value.TryGetInt64(out long milliseconds):
                        deliverAt = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
                        break;
                }
            }

            envelope = new Envelope(id, headers, encoding, groupKey, deliverAt);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private readonly record struct Envelope(string? Id, IReadOnlyDictionary<string, string> Headers, string? Encoding, string? GroupKey, DateTimeOffset? DeliverAt);
}

/// <summary>A message ready to send: <see cref="Size"/> is what SQS and SNS count against their payload limit.</summary>
internal sealed record SqsEncodedMessage(string MessageId, string Body, string Attribute, int Size)
{
    public int DelaySeconds { get; init; }
}

internal sealed record SqsDecodedMessage(string MessageId, byte[] Body, IReadOnlyDictionary<string, string> Headers, string? GroupKey, DateTimeOffset? DeliverAt);
