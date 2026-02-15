using System;

namespace Foundatio.Serializer;

/// <summary>
/// Exception thrown for serialization and deserialization errors.
/// </summary>
public class SerializerException : Exception
{
    public SerializerException(string message) : base(message)
    {
    }

    public SerializerException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
