using System;

namespace Foundatio.Serializer;

/// <summary>
/// Optional support for serializing directly to and from UTF-8 or binary buffers without intermediate streams.
/// Serializer extension methods use this capability automatically when available.
/// </summary>
public interface IBufferSerializer : ISerializer
{
    /// <summary>Serializes a value, including null, to an independently owned byte array.</summary>
    byte[] SerializeToBytes(object? value);

    /// <summary>Deserializes a value without retaining or modifying the input buffer.</summary>
    /// <param name="data">The nonempty serialized data.</param>
    /// <param name="objectType">The type of object to deserialize.</param>
    /// <returns>The deserialized object, or null if the data represents a null value.</returns>
    object? Deserialize(ReadOnlyMemory<byte> data, Type objectType);
}
