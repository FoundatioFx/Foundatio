using System;
using System.Text;

namespace Foundatio.Serializer {
    public interface ISerializer {
        object Deserialize(byte[] data, Type objectType);
        byte[] Serialize(object value);
    }

    public static class SerializerExtensions {
        public static object Deserialize(this ISerializer serializer, string data, Type objectType) {
            return serializer.Deserialize(Encoding.UTF8.GetBytes(data), objectType);
        }

        public static T Deserialize<T>(this ISerializer serializer, byte[] data) {
            return (T)serializer.Deserialize(data, typeof(T));
        }

        public static T Deserialize<T>(this ISerializer serializer, string data) {
            return Deserialize<T>(serializer, Encoding.UTF8.GetBytes(data));
        }

        public static string SerializeToString(this ISerializer serializer, object value) {
            return Encoding.UTF8.GetString(serializer.Serialize(value));
        }
    }
}
