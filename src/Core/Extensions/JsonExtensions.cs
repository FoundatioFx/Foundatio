using System;
using System.CodeDom;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Bson;
using Newtonsoft.Json.Linq;
using NLog.Fluent;

namespace Foundatio.Extensions {
    [System.Runtime.InteropServices.GuidAttribute("4186FC77-AF28-4D51-AAC3-49055DD855A4")]
    public static class JsonExtensions {
        public static bool IsNullOrEmpty(this JToken target) {
            if (target == null || target.Type == JTokenType.Null)
                return true;

            if (target.Type == JTokenType.Object || target.Type == JTokenType.Array)
                return !target.HasValues;

            if (target.Type != JTokenType.Property)
                return false;

            var value = ((JProperty)target).Value;
            if (value.Type == JTokenType.String)
                return String.IsNullOrEmpty(value.ToString());

            return IsNullOrEmpty(value);
        }

        public static bool IsPropertyNullOrEmpty(this JObject target, string name) {
            if (target[name] == null)
                return true;

            return target.Property(name).Value.IsNullOrEmpty();
        }

        public static bool RemoveIfNullOrEmpty(this JObject target, string name) {
            if (!target.IsPropertyNullOrEmpty(name))
                return false;

            target.Remove(name);
            return true;
        }

        public static void RemoveAll(this JObject target, params string[] names) {
            foreach (var name in names)
                target.Remove(name);
        }

        public static bool RemoveAllIfNullOrEmpty(this IEnumerable<JProperty> elements, params string[] names) {
            if (elements == null)
                return false;

            foreach (var p in elements.Where(t => names.Contains(t.Name) && t.IsNullOrEmpty()))
                p.Remove();

            return true;
        }

        public static bool RemoveAllIfNullOrEmpty(this JObject target, params string[] names) {
            if (target.IsNullOrEmpty())
                return false;

            var properties = target.Descendants().OfType<JProperty>().Where(t => names.Contains(t.Name) && t.IsNullOrEmpty()).ToList();
            foreach (var p in properties)
                p.Remove();

            return true;
        }

        public static bool Rename(this JObject target, string currentName, string newName) {
            if (String.Equals(currentName, newName))
                return true;

            if (target[currentName] == null)
                return false;

            JProperty p = target.Property(currentName);
            p.Replace(new JProperty(newName, p.Value));

            return true;
        }

        public static bool RenameOrRemoveIfNullOrEmpty(this JObject target, string currentName, string newName) {
            if (target[currentName] == null)
                return false;

            bool isNullOrEmpty = target.IsPropertyNullOrEmpty(currentName);
            JProperty p = target.Property(currentName);
            if (isNullOrEmpty) {
                target.Remove(p.Name);
                return false;
            }

            p.Replace(new JProperty(newName, p.Value));
            return true;
        }

        public static void MoveOrRemoveIfNullOrEmpty(this JObject target, JObject source, params string[] names) {
            foreach (var name in names) {
                if (source[name] == null)
                    continue;

                bool isNullOrEmpty = source.IsPropertyNullOrEmpty(name);
                JProperty p = source.Property(name);
                source.Remove(p.Name);

                if (isNullOrEmpty)
                    continue;

                target.Add(name, p.Value);
            }
        }

        public static bool RenameAll(this IEnumerable<JProperty> properties, string currentName, string newName) {
            foreach (var p in properties.Where(t => t.Name == currentName)) {
                var parent = p.Parent as JObject;
                if (parent != null)
                    parent.Rename(currentName, newName);
            }

            return true;
        }

        public static bool RenameAll(this JObject target, string currentName, string newName) {
            var properties = target.Descendants().OfType<JProperty>().Where(t => t.Name == currentName).ToList();
            foreach (var p in properties) {
                var parent = p.Parent as JObject;
                if (parent != null)
                    parent.Rename(currentName, newName);
            }

            return true;
        }

        public static string GetPropertyStringValue(this JObject target, string name) {
            if (target.IsPropertyNullOrEmpty(name))
                return null;

            return target.Property(name).Value.ToString();
        }


        public static string GetPropertyStringValueAndRemove(this JObject target, string name) {
            var value = target.GetPropertyStringValue(name);
            target.Remove(name);
            return value;
        }
        public static bool IsJson(this string value) {
            return value.GetJsonType() != JsonType.None;
        }

        public static JsonType GetJsonType(this string value) {
            if (String.IsNullOrEmpty(value))
                return JsonType.None;

            for (int i = 0; i < value.Length; i++) {
                if (Char.IsWhiteSpace(value[i]))
                    continue;

                if (value[i] == '{')
                    return JsonType.Object;

                if (value[i] == '[')
                    return JsonType.Array;

                break;
            }

            return JsonType.None;
        }

        public static string ToJson<T>(this T data, Formatting formatting = Formatting.None, JsonSerializerSettings settings = null) {
            JsonSerializer serializer = settings == null ? JsonSerializer.CreateDefault() : JsonSerializer.CreateDefault(settings);
            serializer.Formatting = formatting;

            using (var sw = new StringWriter()) {
                serializer.Serialize(sw, data, typeof(T));
                return sw.ToString();
            }
        }

        public static List<T> FromJson<T>(this JArray data, JsonSerializerSettings settings = null) {
            JsonSerializer serializer = settings == null ? JsonSerializer.CreateDefault() : JsonSerializer.CreateDefault(settings);
            return data.ToObject<List<T>>(serializer);
        }

        public static T FromJson<T>(this JObject data, JsonSerializerSettings settings = null) {
            JsonSerializer serializer = settings == null ? JsonSerializer.CreateDefault() : JsonSerializer.CreateDefault(settings);
            return data.ToObject<T>(serializer);
        }

        public static object FromJson(this string data, Type objectType, JsonSerializerSettings settings = null) {
            JsonSerializer serializer = settings == null ? JsonSerializer.CreateDefault() : JsonSerializer.CreateDefault(settings);

            using (var sw = new StringReader(data))
            using (var sr = new JsonTextReader(sw))
                return serializer.Deserialize(sr, objectType);
        }

        public static T FromJson<T>(this string data, JsonSerializerSettings settings = null) {
            JsonSerializer serializer = settings == null ? JsonSerializer.CreateDefault() : JsonSerializer.CreateDefault(settings);

            using (var sw = new StringReader(data))
            using (var sr = new JsonTextReader(sw))
                return serializer.Deserialize<T>(sr);
        }

        public static bool TryFromJson<T>(this string data, out T value, JsonSerializerSettings settings = null) {
            try {
                value = data.FromJson<T>(settings);
                return true;
            } catch (Exception ex) {
                Log.Error().Message("Error deserializing json: type={0}", typeof(T).Name).Exception(ex).Write();
                value = default(T);
                return false;
            }
        }

        public static byte[] ToBson<T>(this T data, JsonSerializerSettings settings = null) {
            JsonSerializer serializer = settings == null ? JsonSerializer.CreateDefault() : JsonSerializer.CreateDefault(settings);
            using (var ms = new MemoryStream()) {
                using (var writer = new BsonWriter(ms))
                    serializer.Serialize(writer, data, typeof(T));

                return ms.ToArray();
            }
        }

        public static T FromBson<T>(this byte[] data, JsonSerializerSettings settings = null) {
            JsonSerializer serializer = settings == null ? JsonSerializer.CreateDefault() : JsonSerializer.CreateDefault(settings);

            using (var sw = new MemoryStream(data))
            using (var sr = new BsonReader(sw))
                return serializer.Deserialize<T>(sr);

        }

        public static object FromBson(this byte[] data, Type objectType, JsonSerializerSettings settings = null) {
            JsonSerializer serializer = settings == null ? JsonSerializer.CreateDefault() : JsonSerializer.CreateDefault(settings);

            using (var sw = new MemoryStream(data))
            using (var sr = new BsonReader(sw))
                return serializer.Deserialize(sr, objectType);

        }

        public static bool TryFromBson<T>(this byte[] data, out T value, JsonSerializerSettings settings = null) {
            try {
                value = data.FromBson<T>(settings);
                return true;
            } catch (Exception ex) {
                Log.Error().Message("Error deserializing bson: type={0}", typeof(T).Name).Exception(ex).Write();
                value = default(T);
                return false;
            }
        }

        public static bool TryFromBson(this byte[] data, out object value, Type objectType, JsonSerializerSettings settings = null) {
            try {
                value = data.FromBson(objectType, settings);
                return true;
            } catch (Exception ex) {
                Log.Error().Message("Error deserializing bson: type={0}", objectType.Name).Exception(ex).Write();
                value = null;
                return false;
            }
        }
    }

    public enum JsonType : byte {
        None,
        Object,
        Array
    }
}