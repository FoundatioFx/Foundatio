using System;
using System.Text;
using Newtonsoft.Json;

namespace Foundatio.Serializer {
    public class JsonNetSerializer : ISerializer {
        protected readonly JsonSerializerSettings _settings;

        public JsonNetSerializer(JsonSerializerSettings settings = null) {
            _settings = settings ?? new JsonSerializerSettings();
        }

        public object Deserialize(byte[] value, Type objectType) {
            return JsonConvert.DeserializeObject(Encoding.UTF8.GetString(value), objectType, _settings);
        }

        public byte[] Serialize(object value) {
            if (value == null)
                return null;

            return Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value, _settings));
        }
    }
}
