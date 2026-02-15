using System;
using System.IO;
using Foundatio.Serializer;

namespace Foundatio.Tests.Serializer;

public class FaultInjectingSerializer : ISerializer
{
    private readonly ISerializer _inner;

    public bool ShouldFailOnDeserialize { get; set; }
    public bool ShouldFailOnSerialize { get; set; }

    public FaultInjectingSerializer() : this(null)
    {
    }

    public FaultInjectingSerializer(ISerializer inner)
    {
        _inner = inner ?? DefaultSerializer.Instance;
    }

    public object Deserialize(Stream data, Type objectType)
    {
        if (ShouldFailOnDeserialize)
            throw new SerializerException("Simulated deserialization failure.");

        return _inner.Deserialize(data, objectType);
    }

    public void Serialize(object value, Stream output)
    {
        if (ShouldFailOnSerialize)
            throw new SerializerException("Simulated serialization failure.");

        _inner.Serialize(value, output);
    }
}
