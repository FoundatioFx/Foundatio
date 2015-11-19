using System;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Foundatio.Serializer {
    public class JsonNetSerializer : ISerializer {
        protected readonly JsonSerializerSettings _settings;

        public JsonNetSerializer(JsonSerializerSettings settings = null) {
            _settings = settings ?? new JsonSerializerSettings();
        }

        public Task<object> DeserializeAsync(byte[] value, Type objectType) {
            return Task.FromResult(JsonConvert.DeserializeObject(Encoding.UTF8.GetString(value), objectType, _settings));
        }

        public Task<byte[]> SerializeAsync(object value) {
            if (value == null)
                return Task.FromResult<byte[]>(null);

            return Task.FromResult(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value, _settings)));
        }
    }
}
