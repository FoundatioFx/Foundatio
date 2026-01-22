using System;
using System.IO;
using System.Text;

namespace Foundatio.Serializer;

/// <summary>
/// Defines methods for serializing and deserializing objects to and from streams.
/// </summary>
public interface ISerializer
{
    /// <summary>
    /// Deserializes data from a stream into an object of the specified type.
    /// </summary>
    /// <param name="data">The stream containing the serialized data.</param>
    /// <param name="objectType">The type of object to deserialize.</param>
    /// <returns>The deserialized object.</returns>
    object Deserialize(Stream data, Type objectType);

    /// <summary>
    /// Serializes an object to the specified output stream.
    /// </summary>
    /// <param name="value">The object to serialize. Null values are valid and will be serialized
    /// (e.g., as "null" for JSON serializers or nil markers for binary serializers).</param>
    /// <param name="output">The stream to write the serialized data to.</param>
    void Serialize(object value, Stream output);
}

/// <summary>
/// Marker interface for serializers that produce human-readable text output (e.g., JSON, XML).
/// Text serializers use UTF-8 encoding for string conversions in extension methods.
/// </summary>
public interface ITextSerializer : ISerializer
{
}

public static class DefaultSerializer
{
    public static ITextSerializer Instance { get; set; } = new SystemTextJsonSerializer();
}

public static class SerializerExtensions
{
    public static T Deserialize<T>(this ISerializer serializer, Stream data)
    {
        ArgumentNullException.ThrowIfNull(serializer);
        ArgumentNullException.ThrowIfNull(data);

        return (T)serializer.Deserialize(data, typeof(T));
    }

    public static T Deserialize<T>(this ISerializer serializer, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(serializer);
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length == 0)
            throw new ArgumentException("Data cannot be empty.", nameof(data));

        using var stream = new MemoryStream(data);
        return (T)serializer.Deserialize(stream, typeof(T));
    }

    public static object Deserialize(this ISerializer serializer, byte[] data, Type objectType)
    {
        ArgumentNullException.ThrowIfNull(serializer);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(objectType);
        if (data.Length == 0)
            throw new ArgumentException("Data cannot be empty.", nameof(data));

        using var stream = new MemoryStream(data);
        return serializer.Deserialize(stream, objectType);
    }

    public static T Deserialize<T>(this ISerializer serializer, string data)
    {
        ArgumentNullException.ThrowIfNull(serializer);
        ArgumentException.ThrowIfNullOrWhiteSpace(data);

        var bytes = serializer is ITextSerializer ? Encoding.UTF8.GetBytes(data) : Convert.FromBase64String(data);
        using var stream = new MemoryStream(bytes);
        return (T)serializer.Deserialize(stream, typeof(T));
    }

    public static object Deserialize(this ISerializer serializer, string data, Type objectType)
    {
        ArgumentNullException.ThrowIfNull(serializer);
        ArgumentException.ThrowIfNullOrWhiteSpace(data);
        ArgumentNullException.ThrowIfNull(objectType);

        var bytes = serializer is ITextSerializer ? Encoding.UTF8.GetBytes(data) : Convert.FromBase64String(data);
        using var stream = new MemoryStream(bytes);
        return serializer.Deserialize(stream, objectType);
    }

    public static string SerializeToString<T>(this ISerializer serializer, T value)
    {
        ArgumentNullException.ThrowIfNull(serializer);

        // Serialize null to "null" literal (matches underlying library behavior)
        var bytes = serializer.SerializeToBytes(value);
        return serializer is ITextSerializer ? Encoding.UTF8.GetString(bytes) : Convert.ToBase64String(bytes);
    }

    public static byte[] SerializeToBytes<T>(this ISerializer serializer, T value)
    {
        ArgumentNullException.ThrowIfNull(serializer);

        // Serialize null values - underlying serializers handle this correctly
        // (produces "null" for JSON, nil marker for MessagePack)
        using var stream = new MemoryStream();
        serializer.Serialize(value, stream);

        return stream.ToArray();
    }
}
