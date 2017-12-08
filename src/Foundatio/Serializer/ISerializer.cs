using System;
using System.IO;
using System.Text;

namespace Foundatio.Serializer {
    public interface ISerializer {
        object Deserialize(Stream data, Type objectType);
        void Serialize(object value, Stream output);
    }

    public interface IBinarySerializer : ISerializer {}

    public static class DefaultSerializer {
        public static ISerializer Instance { get; set; } = new MessagePackSerializer();
    }

    public static class SerializerExtensions {
        public static T Deserialize<T>(this ISerializer serializer, Stream data) {
            return (T)serializer.Deserialize(data, typeof(T));
        }

        public static T Deserialize<T>(this ISerializer serializer, string data) {
            var bytes = data != null ? Encoding.UTF8.GetBytes(data) : Array.Empty<byte>();
            return (T)serializer.Deserialize(new MemoryStream(bytes), typeof(T));
        }

        public static object Deserialize(this ISerializer serializer, string data, Type objectType) {
            var bytes = data != null ? Encoding.UTF8.GetBytes(data) : Array.Empty<byte>();
            return serializer.Deserialize(new MemoryStream(bytes), objectType);
        }

        public static T Deserialize<T>(this ISerializer serializer, byte[] data) {
            return (T)serializer.Deserialize(new MemoryStream(data), typeof(T));
        }

        public static object Deserialize(this ISerializer serializer, byte[] data, Type objectType) {
            return serializer.Deserialize(new MemoryStream(data), objectType);
        }

        public static string SerializeToString<T>(this ISerializer serializer, T value) {
            if (serializer is IBinarySerializer)
                throw new ArgumentException("Unable to serialize binary content to a string");

            if (value == null) 
                return null; 
 
            return Encoding.UTF8.GetString(serializer.SerializeToBytes(value)); 
        } 

        public static byte[] SerializeToBytes<T>(this ISerializer serializer, T value) {
            if (value == null)
                return null;

            var stream = new MemoryStream();
            serializer.Serialize(value, stream);

            return stream.ToArray();
        }
    }
}