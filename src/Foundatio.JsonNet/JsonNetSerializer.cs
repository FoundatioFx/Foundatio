using System.IO;
using Newtonsoft.Json;
using System;
using Foundatio.Serializer;

namespace Foundatio.JsonNet {
    public class JsonNetSerializer : ISerializer {
        private readonly JsonSerializer _serializer;
        
        public JsonNetSerializer(JsonSerializerSettings settings = null) {
            _serializer = JsonSerializer.Create(settings ?? new JsonSerializerSettings());
        }

        public void Serialize(object data, Stream outputStream) {
            var writer = new JsonTextWriter(new StreamWriter(outputStream));
            _serializer.Serialize(writer, data, data.GetType());
            writer.Flush();
        }

        public object Deserialize(Stream inputStream, Type objectType) {
            using (var sr = new StreamReader(inputStream))
            using (var reader = new JsonTextReader(sr))
                return _serializer.Deserialize(reader, objectType);
        }
    }
}
