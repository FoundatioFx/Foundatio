using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Utf8Json;
using Utf8Json.Resolvers;

namespace Foundatio.Serializer {
    public class Utf8JsonSerializer : ITextSerializer {
        private readonly IJsonFormatterResolver _formatterResolver;

        public Utf8JsonSerializer(IJsonFormatterResolver resolver = null) {
            _formatterResolver = resolver ?? StandardResolver.Default;
        }

        public void Serialize(object data, Stream outputStream) {
            JsonSerializer.NonGeneric.Serialize(data.GetType(), outputStream, data, _formatterResolver);
        }

        public object Deserialize(Stream inputStream, Type objectType) {
            return JsonSerializer.NonGeneric.Deserialize(objectType, inputStream, _formatterResolver);
        }

        public Task SerializeAsync(object data, Stream outputStream, CancellationToken cancellationToken) {
            return JsonSerializer.NonGeneric.SerializeAsync(data.GetType(), outputStream, data, _formatterResolver);
        }

        public ValueTask<object> DeserializeAsync(Stream inputStream, Type objectType, CancellationToken cancellationToken) {
            return new ValueTask<object>(JsonSerializer.NonGeneric.DeserializeAsync(objectType, inputStream, _formatterResolver));
        }
    }
}