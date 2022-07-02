using System;
using System.Collections.Generic;

namespace Foundatio.Utility {
    public interface IDataDictionary : IDictionary<string, object> { }

    public interface IHaveData {
        IDataDictionary Data { get; }
    }

    public class DataDictionary : Dictionary<string, object>, IDataDictionary {
        public static readonly DataDictionary Empty = new();

        public DataDictionary() : base(StringComparer.OrdinalIgnoreCase) {}

        public DataDictionary(IEnumerable<KeyValuePair<string, object>> values) : base(StringComparer.OrdinalIgnoreCase) {
            if (values != null) {
                foreach (var kvp in values)
                    Add(kvp.Key, kvp.Value);
            }
        }
    }

    public static class DataDictionaryExtensions {
        public static object GetValueOrDefault(this IDataDictionary dictionary, string key) {
            return dictionary.TryGetValue(key, out object value) ? value : null;
        }

        public static object GetValueOrDefault(this IDataDictionary dictionary, string key, object defaultValue) {
            return dictionary.TryGetValue(key, out object value) ? value : defaultValue;
        }

        public static object GetValueOrDefault(this IDataDictionary dictionary, string key, Func<object> defaultValueProvider) {
            return dictionary.TryGetValue(key, out object value) ? value : defaultValueProvider();
        }

        public static T GetValue<T>(this IDataDictionary dictionary, string key) {
            if (!dictionary.ContainsKey(key))
                throw new KeyNotFoundException($"Key \"{key}\" not found in the dictionary");

            return dictionary.GetValueOrDefault<T>(key);
        }

        public static T GetValueOrDefault<T>(this IDataDictionary dictionary, string key, T defaultValue = default) {
            if (!dictionary.ContainsKey(key))
                return defaultValue;

            object data = dictionary[key];
            if (data is T t)
                return t;

            if (data == null)
                return defaultValue;

            try {
                return data.ToType<T>();
            } catch { }

            return defaultValue;
        }

        public static string GetString(this IDataDictionary dictionary, string name) {
            return dictionary.GetString(name, String.Empty);
        }

        public static string GetString(this IDataDictionary dictionary, string name, string @default) {
            if (!dictionary.TryGetValue(name, out object value))
                return @default;

            if (value is string s)
                return s;

            return String.Empty;
        }
    }
}