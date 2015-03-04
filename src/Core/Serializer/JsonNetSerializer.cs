using System;
using Newtonsoft.Json;

namespace Foundatio.Serializer {
    public class JsonNetSerializer : ISerializer {
        protected readonly JsonSerializerSettings _settings;

        public JsonNetSerializer(JsonSerializerSettings settings = null) {
            _settings = settings ?? new JsonSerializerSettings();
        }

        public T Deserialize<T>(string value) {
            return JsonConvert.DeserializeObject<T>(value, _settings);
        }

        public object Deserialize(string value, Type objectType) {
            return JsonConvert.DeserializeObject(value, objectType, _settings);
        }

        public string Serialize(object value) {
            return JsonConvert.SerializeObject(value, _settings);
        }
    }
}
