using System;
using Foundatio.Extensions;
using Foundatio.Serializer;
using Foundatio.Utility;
using StackExchange.Redis;

namespace Foundatio.Redis {
    internal static class RedisValueExtensions {
        private static readonly RedisValue _nullValue = "@@NULL";

        public static T ToValueOfType<T>(this RedisValue redisValue, ISerializer serializer) {
            T value;
            var type = typeof(T);

            if (type == TypeHelper.BoolType || type == TypeHelper.StringType || type.IsNumeric())
                value = (T)Convert.ChangeType(redisValue, type);
            else if (type == TypeHelper.NullableBoolType || type.IsNullableNumeric())
                value = redisValue.IsNull ? default : (T)Convert.ChangeType(redisValue, Nullable.GetUnderlyingType(type));
            else
                return serializer.Deserialize<T>((byte[])redisValue);

            return value;
        }

        public static RedisValue ToRedisValue<T>(this T value, ISerializer serializer) {
            var redisValue = _nullValue;
            if (value == null)
                return redisValue;

            var t = typeof(T);
            if (t == TypeHelper.StringType)
                redisValue = value.ToString();
            else if (t == TypeHelper.BoolType)
                redisValue = Convert.ToBoolean(value);
            else if (t == TypeHelper.ByteType)
                redisValue = Convert.ToByte(value);
            else if (t == TypeHelper.Int16Type)
                redisValue = Convert.ToInt16(value);
            else if (t == TypeHelper.Int32Type)
                redisValue = Convert.ToInt32(value);
            else if (t == TypeHelper.Int64Type)
                redisValue = Convert.ToInt64(value);
            else if (t == TypeHelper.DoubleType)
                redisValue = Convert.ToDouble(value);
            else if (t == TypeHelper.StringType)
                redisValue = value.ToString();
            else if (t == TypeHelper.CharType)
                redisValue = Convert.ToChar(value);
            else if (t == TypeHelper.SByteType)
                redisValue = Convert.ToSByte(value);
            else if (t == TypeHelper.UInt16Type)
                redisValue = Convert.ToUInt16(value);
            else if (t == TypeHelper.UInt32Type)
                redisValue = Convert.ToUInt32(value);
            else if (t == TypeHelper.UInt64Type)
                redisValue = Convert.ToUInt64(value);
            else if (t == TypeHelper.SingleType)
                redisValue = Convert.ToSingle(value);
            //else if (type == TypeHelper.DecimalType)
            //    redisValue = Convert.ToDecimal(value);
            //else if (type == TypeHelper.DateTimeType)
            //    redisValue = Convert.ToDateTime(value);
            else if (t == TypeHelper.ByteArrayType)
                redisValue = value as byte[];
            else
                redisValue = serializer.SerializeToBytes(value);

            return redisValue;
        }
    }

    public static class RedisExtensions {
        public static bool IsCluster(this ConnectionMultiplexer muxer) {
            var configuration = ConfigurationOptions.Parse(muxer.Configuration);
            if (configuration.Proxy == Proxy.Twemproxy)
                return true;

            int standaloneCount = 0, clusterCount = 0, sentinelCount = 0;
            foreach (var endPoint in muxer.GetEndPoints()) {
                var server = muxer.GetServer(endPoint);
                if (server.IsConnected) {
                    // count the server types
                    switch (server.ServerType) {
                        case ServerType.Twemproxy:
                        case ServerType.Standalone:
                            standaloneCount++;
                            break;
                        case ServerType.Sentinel:
                            sentinelCount++;
                            break;
                        case ServerType.Cluster:
                            clusterCount++;
                            break;
                    }
                }
            }

            if (clusterCount != 0)
                return true;

            if (standaloneCount == 0 && sentinelCount > 0)
                return true;

            return false;
        }
    }
}
