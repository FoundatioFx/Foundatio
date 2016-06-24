using System;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Serializer;
using StackExchange.Redis;

namespace Foundatio.Redis {
    public static class RedisValueExtensions {
        private static readonly RedisValue _nullValue = "@@NULL";

        public static Task<T> ToValueOfType<T>(this RedisValue redisValue, ISerializer serializer) {
            T value;
            if (typeof(T) == typeof(Int16) || typeof(T) == typeof(Int32) || typeof(T) == typeof(Int64) ||
                typeof(T) == typeof(bool) || typeof(T) == typeof(double) || typeof(T) == typeof(string))
                value = (T)Convert.ChangeType(redisValue, typeof(T));
            else if (typeof(T) == typeof(Int16?) || typeof(T) == typeof(Int32?) || typeof(T) == typeof(Int64?) ||
                     typeof(T) == typeof(bool?) || typeof(T) == typeof(double?))
                value = redisValue.IsNull
                    ? default(T)
                    : (T)Convert.ChangeType(redisValue, Nullable.GetUnderlyingType(typeof(T)));
            else
                return serializer.DeserializeAsync<T>(redisValue.ToString());

            return Task.FromResult(value);
        }

        public static async Task<RedisValue> ToRedisValueAsync<T>(this T value, ISerializer serializer) {
            RedisValue redisValue = _nullValue;

            if (value == null) return redisValue;

            if (typeof(T) == typeof(Int16))
                redisValue = Convert.ToInt16(value);
            else if (typeof(T) == typeof(Int32))
                redisValue = Convert.ToInt32(value);
            else if (typeof(T) == typeof(Int64))
                redisValue = Convert.ToInt64(value);
            else if (typeof(T) == typeof(bool))
                redisValue = Convert.ToBoolean(value);
            else if (typeof(T) == typeof(string))
                redisValue = value?.ToString();
            else
                redisValue = await serializer.SerializeAsync(value).AnyContext();

            return redisValue;
        }
    }
}
