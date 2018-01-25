using System;
using System.IO;
using MessagePack;
using MessagePack.Resolvers;

namespace Foundatio.Serializer {
    public class MessagePackSerializer : ISerializer {
        private readonly IFormatterResolver _formatterResolver;
        private readonly bool _compressEnabled;

        public MessagePackSerializer(IFormatterResolver resolver = null, bool compressEnabled = false) {
            _compressEnabled = compressEnabled;
            _formatterResolver = resolver ?? ContractlessStandardResolver.Instance;
        }

        public void Serialize(object data, Stream output) {
            if (_compressEnabled) {
                MessagePack.LZ4MessagePackSerializer.NonGeneric.Serialize(data.GetType(), output, data, _formatterResolver);
            }
            else {
                MessagePack.MessagePackSerializer.NonGeneric.Serialize(data.GetType(), output, data, _formatterResolver);
            }
        }

        public object Deserialize(Stream input, Type objectType) {
            if (_compressEnabled) {
                return MessagePack.LZ4MessagePackSerializer.NonGeneric.Deserialize(objectType, input, _formatterResolver);
            }
            else {
                return MessagePack.MessagePackSerializer.NonGeneric.Deserialize(objectType, input, _formatterResolver);
            }
        }
    }
}