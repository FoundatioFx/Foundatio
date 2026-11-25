using System;
using System.IO;
using System.Text.Json;

namespace Foundatio.Serializer;

public class SystemTextJsonSerializer : ITextSerializer
{
    private readonly JsonSerializerOptions _serializeOptions;
    private readonly JsonSerializerOptions _deserializeOptions;

    public SystemTextJsonSerializer(JsonSerializerOptions? serializeOptions = null, JsonSerializerOptions? deserializeOptions = null)
    {
        _serializeOptions = serializeOptions ?? JsonSerializerOptions.Default;
        _deserializeOptions = deserializeOptions ?? serializeOptions ?? JsonSerializerOptions.Default;
    }

    public void Serialize(object? value, Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);

        // Use direct stream serialization (more efficient than Utf8JsonWriter)
        // Handles null values correctly (writes "null")
        JsonSerializer.Serialize(output, value, value?.GetType() ?? typeof(object), _serializeOptions);
    }

    public object? Deserialize(Stream data, Type objectType)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(objectType);

        // Use direct stream deserialization (avoids string allocation)
        object? result = JsonSerializer.Deserialize(data, objectType, _deserializeOptions);

        // Handle primitive types when deserializing to object
        if (result is JsonElement jsonElement)
            return ConvertJsonElement(jsonElement);

        return result;
    }

    /// <summary>The same bytes as <see cref="Serialize"/> writes, without a stream and the copy out of it.</summary>
    internal byte[] SerializeToUtf8Bytes(object? value)
    {
        return JsonSerializer.SerializeToUtf8Bytes(value, value?.GetType() ?? typeof(object), _serializeOptions);
    }

    /// <summary>The same result as <see cref="Deserialize(Stream, Type)"/> over these bytes, read in place.</summary>
    internal object? Deserialize(ReadOnlySpan<byte> data, Type objectType)
    {
        // A stream read skips a UTF-8 byte order mark; reading the bytes directly does not.
        if (data.StartsWith(Utf8ByteOrderMark))
            data = data[Utf8ByteOrderMark.Length..];

        object? result = JsonSerializer.Deserialize(data, objectType, _deserializeOptions);
        if (result is JsonElement jsonElement)
            return ConvertJsonElement(jsonElement);

        return result;
    }

    private static ReadOnlySpan<byte> Utf8ByteOrderMark => [0xEF, 0xBB, 0xBF];

    private static object? ConvertJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => GetNumber(element),
            JsonValueKind.String => GetStringOrDateTime(element),
            _ => element // Array/Object remain as JsonElement
        };
    }

    private static object GetNumber(JsonElement element)
    {
        // Try smallest to largest integer types first for optimal boxing
        if (element.TryGetInt32(out int i))
            return i;

        if (element.TryGetInt64(out long l))
            return l;

        // Try decimal for precise values (e.g., financial data) before double
        if (element.TryGetDecimal(out decimal d))
            return d;

        return element.GetDouble();
    }

    private static object? GetStringOrDateTime(JsonElement element)
    {
        // Try DateTimeOffset first (more specific, preserves timezone info)
        if (element.TryGetDateTimeOffset(out var dto))
            return dto;

        if (element.TryGetDateTime(out var dt))
            return dt;

        return element.GetString();
    }
}
