using System;
using System.IO;
using System.Text.Json;

namespace Foundatio.Serializer {
    public class SystemTextJsonSerializer : ITextSerializer {
        private readonly JsonSerializerOptions _options;

        public SystemTextJsonSerializer(JsonSerializerOptions options = null) {
            _options = options;
        }

        public void Serialize(object data, Stream outputStream) {
            var writer = new Utf8JsonWriter(outputStream);
            JsonSerializer.Serialize(writer, data, data.GetType(), _options);
            writer.Flush();
        }

        public object Deserialize(Stream inputStream, Type objectType) {
            using (var reader = new StreamReader(inputStream))
                return JsonSerializer.Deserialize(reader.ReadToEnd(), objectType, _options);
        }
    }
}
