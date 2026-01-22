using System;
using System.IO;
using MessagePack;
using MessagePack.Resolvers;

namespace Foundatio.Serializer;

public class MessagePackSerializer : ISerializer
{
    private readonly MessagePackSerializerOptions _options;

    public MessagePackSerializer(MessagePackSerializerOptions options = null)
    {
        _options = options ?? MessagePackSerializerOptions.Standard.WithResolver(ContractlessStandardResolver.Instance);
    }

    public void Serialize(object value, Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);

        MessagePack.MessagePackSerializer.Serialize(value?.GetType() ?? typeof(object), output, value, _options);
    }

    public object Deserialize(Stream data, Type objectType)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(objectType);

        return MessagePack.MessagePackSerializer.Deserialize(objectType, data, _options);
    }
}
