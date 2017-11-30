using System;
using System.IO;
using System.Text;

namespace Foundatio.Serializer {
    public interface ISerializer {
        object Deserialize(byte[] data, Type objectType);
        byte[] Serialize(object value);
    }

    public static class SerializerExtensions {
        public static object Deserialize(this ISerializer serializer, string data, Type objectType) {
            return serializer.Deserialize(Encoding.UTF8.GetBytes(data ?? String.Empty), objectType);
        }

        public static T Deserialize<T>(this ISerializer serializer, byte[] data) {
            return (T)serializer.Deserialize(data, typeof(T));
        }

        public static T Deserialize<T>(this ISerializer serializer, Stream stream) {
            if (stream is MemoryStream memoryStream)
                return (T)serializer.Deserialize(memoryStream.ToArray(), typeof(T));

            using (var destination = new MemoryStream()) {
                stream.CopyTo(destination);
                return (T)serializer.Deserialize(destination.ToArray(), typeof(T));
            }
        }

        public static T Deserialize<T>(this ISerializer serializer, string data) {
            return Deserialize<T>(serializer, Encoding.UTF8.GetBytes(data ?? String.Empty));
        }

        public static string SerializeToString(this ISerializer serializer, object value) {
            if (value == null)
                return null;

            return Encoding.UTF8.GetString(serializer.Serialize(value));
        }

        public static Stream SerializeToStream(this ISerializer serializer, object value) {
            if (value == null)
                return null;

            return new MemoryStream(serializer.Serialize(value));
        }
    }
}