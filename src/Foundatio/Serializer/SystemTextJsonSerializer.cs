using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Foundatio.Serializer {
    public class SystemTextJsonSerializer : ITextSerializer {
        private readonly JsonSerializerOptions _options;

        public SystemTextJsonSerializer(JsonSerializerOptions options = null) {
            if (options != null) {
                _options = options;
            } else {
                _options = new JsonSerializerOptions();
                _options.Converters.Add(new ObjectToInferredTypesConverter());
            }
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

    public class ObjectToInferredTypesConverter : JsonConverter<object> {
        public override object Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
            if (reader.TokenType == JsonTokenType.True)
                return true;

            if (reader.TokenType == JsonTokenType.False)
                return false;

            if (reader.TokenType == JsonTokenType.Number) {
                if (reader.TryGetInt64(out long l))
                    return l;

                return reader.GetDouble();
            }

            if (reader.TokenType == JsonTokenType.String) {
                if (reader.TryGetDateTime(out DateTime datetime))
                    return datetime;

                return reader.GetString();
            }

            using JsonDocument document = JsonDocument.ParseValue(ref reader);
            return document.RootElement.Clone();
        }

        public override void Write(Utf8JsonWriter writer, object objectToWrite, JsonSerializerOptions options) => throw new InvalidOperationException("Should not get here.");
    }
}
