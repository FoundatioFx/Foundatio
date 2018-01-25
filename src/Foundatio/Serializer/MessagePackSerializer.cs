using System;
using System.IO;
using MessagePack;
using MessagePack.Resolvers;

namespace Foundatio.Serializer {
    public class MessagePackSerializer : ISerializer {
        private readonly IFormatterResolver _formatterResolver;
        private readonly bool _useCompression;

        public MessagePackSerializer(IFormatterResolver resolver = null, bool useCompression = false) {
            _useCompression = useCompression;
            _formatterResolver = resolver ?? ContractlessStandardResolver.Instance;
        }

        public void Serialize(object data, Stream output) {
            if (_useCompression)
                MessagePack.LZ4MessagePackSerializer.NonGeneric.Serialize(data.GetType(), output, data, _formatterResolver);
            else
                MessagePack.MessagePackSerializer.NonGeneric.Serialize(data.GetType(), output, data, _formatterResolver);
        }

        public object Deserialize(Stream input, Type objectType) {
            if (_useCompression)
                return MessagePack.LZ4MessagePackSerializer.NonGeneric.Deserialize(objectType, input, _formatterResolver);
            else
                return MessagePack.MessagePackSerializer.NonGeneric.Deserialize(objectType, input, _formatterResolver);
        }
    }
}