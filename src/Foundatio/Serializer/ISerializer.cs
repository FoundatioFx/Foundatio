using System;
using System.IO;
using System.Text;

namespace Foundatio.Serializer;

public interface ISerializer
{
    object Deserialize(Stream data, Type objectType);
    void Serialize(object value, Stream output);
}

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

        return (T)serializer.Deserialize(new MemoryStream(data), typeof(T));
    }

    public static object Deserialize(this ISerializer serializer, byte[] data, Type objectType)
    {
        ArgumentNullException.ThrowIfNull(serializer);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(objectType);
        ArgumentOutOfRangeException.ThrowIfZero(data.Length);

        return serializer.Deserialize(new MemoryStream(data), objectType);
    }

    public static T Deserialize<T>(this ISerializer serializer, string data)
    {
        ArgumentNullException.ThrowIfNull(serializer);
        ArgumentException.ThrowIfNullOrEmpty(data);

        var bytes = serializer is ITextSerializer ? Encoding.UTF8.GetBytes(data) : Convert.FromBase64String(data);
        return (T)serializer.Deserialize(new MemoryStream(bytes), typeof(T));
    }

    public static object Deserialize(this ISerializer serializer, string data, Type objectType)
    {
        ArgumentNullException.ThrowIfNull(serializer);
        ArgumentException.ThrowIfNullOrEmpty(data);
        ArgumentNullException.ThrowIfNull(objectType);

        var bytes = serializer is ITextSerializer ? Encoding.UTF8.GetBytes(data) : Convert.FromBase64String(data);
        return serializer.Deserialize(new MemoryStream(bytes), objectType);
    }

    public static string SerializeToString<T>(this ISerializer serializer, T value)
    {
        ArgumentNullException.ThrowIfNull(serializer);

        if (value is null)
            return null;

        var bytes = serializer.SerializeToBytes(value);
        return serializer is ITextSerializer ? Encoding.UTF8.GetString(bytes) : Convert.ToBase64String(bytes);
    }

    public static byte[] SerializeToBytes<T>(this ISerializer serializer, T value)
    {
        ArgumentNullException.ThrowIfNull(serializer);

        if (value is null)
            return null;

        var stream = new MemoryStream();
        serializer.Serialize(value, stream);

        return stream.ToArray();
    }
}
