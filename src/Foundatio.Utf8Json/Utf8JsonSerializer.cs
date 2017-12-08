using System;
using System.IO;
using Utf8Json;
using Utf8Json.Resolvers;

namespace Foundatio.Serializer {
    public class Utf8JsonSerializer : ITextSerializer {
        private readonly IJsonFormatterResolver _formatterResolver;

        public Utf8JsonSerializer(IJsonFormatterResolver resolver = null) {
            _formatterResolver = resolver ?? StandardResolver.Default;
        }

        public void Serialize(object data, Stream output) {
            JsonSerializer.NonGeneric.Serialize(data.GetType(), output, data, _formatterResolver);
        }

        public object Deserialize(Stream input, Type objectType) {
            return JsonSerializer.NonGeneric.Deserialize(objectType, input, _formatterResolver);
        }
    }
}