using System;
using System.Text;
using System.Threading.Tasks;
using Foundatio.Extensions;

namespace Foundatio.Serializer {
    public interface ISerializer {
        Task<object> DeserializeAsync(byte[] data, Type objectType);
        Task<byte[]> SerializeAsync(object value);
    }

    public static class SerializerExtensions {
        public static Task<object> DeserializeAsync(this ISerializer serializer, string data, Type objectType) {
            return serializer.DeserializeAsync(Encoding.UTF8.GetBytes(data ?? String.Empty), objectType);
        }

        public static async Task<T> DeserializeAsync<T>(this ISerializer serializer, byte[] data) {
            return (T)await serializer.DeserializeAsync(data, typeof(T)).AnyContext();
        }

        public static Task<T> DeserializeAsync<T>(this ISerializer serializer, string data) {
            return DeserializeAsync<T>(serializer, Encoding.UTF8.GetBytes(data ?? String.Empty));
        }

        public static async Task<string> SerializeToStringAsync(this ISerializer serializer, object value) {
            if (value == null)
                return null;

            return Encoding.UTF8.GetString(await serializer.SerializeAsync(value).AnyContext());
        }
    }
}
