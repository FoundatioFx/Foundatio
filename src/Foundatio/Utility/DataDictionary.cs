using System;
using System.Collections.Generic;

namespace Foundatio.Utility {
    public class DataDictionary : Dictionary<string, object> {
        public static readonly DataDictionary Empty = new();

        public DataDictionary() : base(StringComparer.OrdinalIgnoreCase) {}

        public DataDictionary(IEnumerable<KeyValuePair<string, object>> values) : base(StringComparer.OrdinalIgnoreCase) {
            foreach (var kvp in values)
                Add(kvp.Key, kvp.Value);
        }

        public object GetValueOrDefault(string key) {
            return TryGetValue(key, out object value) ? value : null;
        }

        public object GetValueOrDefault(string key, object defaultValue) {
            return TryGetValue(key, out object value) ? value : defaultValue;
        }

        public object GetValueOrDefault(string key, Func<object> defaultValueProvider) {
            return TryGetValue(key, out object value) ? value : defaultValueProvider();
        }

        public T GetValue<T>(string key) {
            if (!ContainsKey(key))
                throw new KeyNotFoundException($"Key \"{key}\" not found in the dictionary");

            return GetValueOrDefault<T>(key);
        }

        public T GetValueOrDefault<T>(string key, T defaultValue = default) {
            if (!ContainsKey(key))
                return defaultValue;

            object data = this[key];
            if (data is T)
                return (T)data;

            if (data == null)
                return defaultValue;

            try {
                return data.ToType<T>();
            } catch {}

            return defaultValue;
        }

        public string GetString(string name) {
            return GetString(name, String.Empty);
        }

        public string GetString(string name, string @default) {
            if (!TryGetValue(name, out object value))
                return @default;

            if (value is string)
                return (string)value;

            return String.Empty;
        }
    }
}