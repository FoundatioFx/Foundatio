using System;
using System.IO;
using Foundatio.Serializer;
using Utf8Json;
using Utf8Json.Resolvers;

namespace Foundatio.Utf8Json {
    public class Utf8JsonSerializer : ISerializer {
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