using System;
using System.IO;
using MessagePack;
using MessagePack.Resolvers;

namespace Foundatio.Serializer {
    public class MessagePackSerializer : ISerializer {
        private readonly MessagePackSerializerOptions _options;

        public MessagePackSerializer(MessagePackSerializerOptions options = null) {
            _options = options ?? MessagePackSerializerOptions.Standard.WithResolver(ContractlessStandardResolver.Instance);
        }

        public void Serialize(object data, Stream output) {
            MessagePack.MessagePackSerializer.Serialize(data.GetType(), output, data, _options);
        }

        public object Deserialize(Stream input, Type objectType) {
            return MessagePack.MessagePackSerializer.Deserialize(objectType, input, _options);
        }
    }
}