using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Serializer {
    public interface ISerializer {
        void Serialize(object value, Stream outputStream);
        object Deserialize(Stream inputStream, Type objectType);
        Task SerializeAsync(object data, Stream outputStream, CancellationToken cancellationToken);
        ValueTask<object> DeserializeAsync(Stream inputStream, Type objectType, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Marker interface indicating that the underlying serialization format is text based (ie. JSON)
    /// </summary>
    public interface ITextSerializer : ISerializer {}

    public static class DefaultSerializer {
        public static ISerializer Instance { get; set; } = new SystemTextJsonSerializer();
    }

    public static class SerializerExtensions {
        public static T Deserialize<T>(this ISerializer serializer, Stream data) {
            return (T)serializer.Deserialize(data, typeof(T));
        }

        public static T Deserialize<T>(this ISerializer serializer, byte[] data) {
            return (T)serializer.Deserialize(new MemoryStream(data), typeof(T));
        }

        public static object Deserialize(this ISerializer serializer, byte[] data, Type objectType) {
            return serializer.Deserialize(new MemoryStream(data), objectType);
        }

        public static T Deserialize<T>(this ISerializer serializer, string data) {
            byte[] bytes;
            if (data == null)
                bytes = Array.Empty<byte>();
            else if (serializer is ITextSerializer)
                bytes = Encoding.UTF8.GetBytes(data);
            else
                bytes = Convert.FromBase64String(data);

            return (T)serializer.Deserialize(new MemoryStream(bytes), typeof(T));
        }

        public static object Deserialize(this ISerializer serializer, string data, Type objectType) {
            byte[] bytes;
            if (data == null)
                bytes = Array.Empty<byte>();
            else if (serializer is ITextSerializer)
                bytes = Encoding.UTF8.GetBytes(data);
            else
                bytes = Convert.FromBase64String(data);

            return serializer.Deserialize(new MemoryStream(bytes), objectType);
        }

        public static string SerializeToString<T>(this ISerializer serializer, T value) {
            if (value == null) 
                return null;

            var bytes = serializer.SerializeToBytes(value);
            if (serializer is ITextSerializer)
                return Encoding.UTF8.GetString(bytes);

            return Convert.ToBase64String(bytes); 
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