using System;
using System.Collections.Generic;
using System.Linq;
using Foundatio.Extensions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Foundatio.Utility {
    public class DataDictionary : Dictionary<string, object> {
        public DataDictionary() : base(StringComparer.OrdinalIgnoreCase) {}

        public DataDictionary(IEnumerable<KeyValuePair<string, object>> values) : base(StringComparer.OrdinalIgnoreCase) {
            foreach (var kvp in values)
                Add(kvp.Key, kvp.Value);
        }

        public object GetValueOrDefault(string key) {
            object value;
            return TryGetValue(key, out value) ? value : null;
        }

        public object GetValueOrDefault(string key, object defaultValue) {
            object value;
            return TryGetValue(key, out value) ? value : defaultValue;
        }

        public object GetValueOrDefault(string key, Func<object> defaultValueProvider) {
            object value;
            return TryGetValue(key, out value) ? value : defaultValueProvider();
        }

        public T GetValue<T>(string key) {
            if (!ContainsKey(key))
                throw new KeyNotFoundException($"Key \"{key}\" not found in the dictionary.");

            return GetValueOrDefault<T>(key);
        }

        public T GetValueOrDefault<T>(string key, T defaultValue = default(T)) {
            if (!ContainsKey(key))
                return defaultValue;

            object data = this[key];
            if (data is T)
                return (T)data;

            if (data is string) {
                try {
                    return JsonConvert.DeserializeObject<T>((string)data);
                } catch {}
            }

            if (data is JObject) {
                try {
                    return JsonConvert.DeserializeObject<T>(data.ToString());
                } catch {}
            }

            try {
                return data.ToType<T>();
            } catch {}

            return defaultValue;
        }

        public string GetString(string name) {
            return GetString(name, String.Empty);
        }

        public string GetString(string name, string @default) {
            object value;

            if (!TryGetValue(name, out value))
                return @default;

            if (value is string)
                return (string)value;

            return String.Empty;
        }

        public void ConvertJObjectToDataDictionary() {
            foreach (var kvp in this.ToList()) {
                if (kvp.Value is JObject) {
                    this[kvp.Key] = DeserializeToDictionary(kvp.Value.ToString());
                } else {
                    this[kvp.Key] = kvp.Value;
                }
            }
        }

        public void RemoveSensitiveData() {
            Keys.Where(k => k.StartsWith("-")).ToArray().ForEach(key => Remove(key));
        }

        private static Dictionary<string, object> DeserializeToDictionary(string json) {
            var values = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
            var values2 = new Dictionary<string, object>();
            foreach (KeyValuePair<string, object> d in values) {
                if (d.Value is JObject) {
                    values2.Add(d.Key, DeserializeToDictionary(d.Value.ToString()));
                } else {
                    values2.Add(d.Key, d.Value);
                }
            }

            return values2;
        }
    }
}