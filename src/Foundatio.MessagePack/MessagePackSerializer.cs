using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MessagePack;
using MessagePack.Resolvers;

namespace Foundatio.Serializer {
    public class MessagePackSerializer : ISerializer {
        private readonly MessagePackSerializerOptions _options;

        public MessagePackSerializer(MessagePackSerializerOptions options = null) {
            _options = options ?? MessagePackSerializerOptions.Standard.WithResolver(ContractlessStandardResolver.Instance);
        }

        public void Serialize(object data, Stream outputStream) {
            MessagePack.MessagePackSerializer.Serialize(data.GetType(), outputStream, data, _options);
        }

        public object Deserialize(Stream inputStream, Type objectType) {
            return MessagePack.MessagePackSerializer.Deserialize(objectType, inputStream, _options);
        }

        public Task SerializeAsync(object data, Stream outputStream, CancellationToken cancellationToken) {
            return MessagePack.MessagePackSerializer.SerializeAsync(data.GetType(), outputStream, data, _options);
        }

        public ValueTask<object> DeserializeAsync(Stream inputStream, Type objectType, CancellationToken cancellationToken) {
            return MessagePack.MessagePackSerializer.DeserializeAsync(objectType, inputStream, _options);
        }
    }
}