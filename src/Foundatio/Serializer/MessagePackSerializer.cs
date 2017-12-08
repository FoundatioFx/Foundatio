using System;
using System.IO;
using MessagePack;
using MessagePack.Resolvers;

namespace Foundatio.Serializer {
    public class MessagePackSerializer : ISerializer {
        public static ISerializer Default = new MessagePackSerializer();

        private readonly IFormatterResolver _formatterResolver;

        public MessagePackSerializer(IFormatterResolver resolver = null) {
            _formatterResolver = resolver ?? ContractlessStandardResolver.Instance;
        }

        public void Serialize(object data, Stream output) {
            MessagePack.MessagePackSerializer.NonGeneric.Serialize(data.GetType(), output, data, _formatterResolver);
        }

        public object Deserialize(Stream input, Type objectType) {
            return MessagePack.MessagePackSerializer.NonGeneric.Deserialize(objectType, input, _formatterResolver);
        }
    }
}