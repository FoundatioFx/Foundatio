using System;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Serializer;
using Foundatio.Utility;
using StackExchange.Redis;

namespace Foundatio.Redis {
    internal static class RedisValueExtensions {
        private static readonly RedisValue _nullValue = "@@NULL";

        public static Task<T> ToValueOfType<T>(this RedisValue redisValue, ISerializer serializer) {
            T value;
            Type type = typeof(T);

            if (type == TypeHelper.BoolType || type == TypeHelper.StringType || type.IsNumeric())
                value = (T)Convert.ChangeType(redisValue, type);
            else if (type == TypeHelper.NullableBoolType || type.IsNullableNumeric())
                value = redisValue.IsNull ? default(T) : (T)Convert.ChangeType(redisValue, Nullable.GetUnderlyingType(type));
            else
                return serializer.DeserializeAsync<T>(redisValue.ToString());

            return Task.FromResult(value);
        }

        public static async Task<RedisValue> ToRedisValueAsync<T>(this T value, ISerializer serializer) {
            RedisValue redisValue = _nullValue;
            if (value == null)
                return redisValue;

            Type t = typeof(T);
            if (t == TypeHelper.ByteArrayType)
                redisValue = value as byte[];
            else if (t == TypeHelper.BoolType)
                redisValue = Convert.ToBoolean(value);
            else if (t == TypeHelper.CharType)
                redisValue = Convert.ToChar(value);
            else if (t == TypeHelper.SByteType)
                redisValue = Convert.ToSByte(value);
            else if (t == TypeHelper.ByteType)
                redisValue = Convert.ToByte(value);
            else if (t == TypeHelper.Int16Type)
                redisValue = Convert.ToInt16(value);
            else if (t == TypeHelper.UInt16Type)
                redisValue = Convert.ToUInt16(value);
            else if (t == TypeHelper.Int32Type)
                redisValue = Convert.ToInt32(value);
            else if (t == TypeHelper.UInt32Type)
                redisValue = Convert.ToUInt32(value);
            else if (t == TypeHelper.Int64Type)
                redisValue = Convert.ToInt64(value);
            else if (t == TypeHelper.UInt64Type)
                redisValue = Convert.ToUInt64(value);
            else if (t == TypeHelper.SingleType)
                redisValue = Convert.ToSingle(value);
            else if (t == TypeHelper.DoubleType)
                redisValue = Convert.ToDouble(value);
            //else if (type == TypeHelper.DecimalType)
            //    redisValue = Convert.ToDecimal(value);
            //else if (type == TypeHelper.DateTimeType)
            //    redisValue = Convert.ToDateTime(value);
            else if (t == TypeHelper.StringType)
                redisValue = value.ToString();
            else
                redisValue = await serializer.SerializeAsync(value).AnyContext();

            return redisValue;
        }
    }
}
