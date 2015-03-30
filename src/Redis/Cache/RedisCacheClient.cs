using System;
using System.Collections.Generic;
using System.Linq;
using Foundatio.Caching;
using Foundatio.Serializer;
using StackExchange.Redis;

namespace Foundatio.Redis.Cache {
    public class RedisCacheClient : ICacheClient, IHaveSerializer {
        private readonly ConnectionMultiplexer _connectionMultiplexer;
        private readonly IDatabase _db;
        private readonly ISerializer _serializer;

        public RedisCacheClient(ConnectionMultiplexer connectionMultiplexer, ISerializer serializer = null) {
            _connectionMultiplexer = connectionMultiplexer;
            _db = connectionMultiplexer.GetDatabase();
            _serializer = serializer ?? new JsonNetSerializer();
        }

        public bool Remove(string key) {
            if (String.IsNullOrEmpty(key))
                return true;

            return _db.KeyDelete(key);
        }

        public void RemoveAll(IEnumerable<string> keys) {
            var redisKeys = keys.Where(k => !String.IsNullOrEmpty(k)).Select(k => (RedisKey)k).ToArray();
            if (redisKeys.Length > 0)
                _db.KeyDelete(redisKeys);
        }

        public T Get<T>(string key) {
            return ChangeType<T>(_db.StringGet(key));
        }

        public bool TryGet<T>(string key, out T value) {
            return InternalTryGet(key, out value);
        }

        protected bool InternalTryGet<T>(string key, out T value, CommandFlags flags = CommandFlags.None) {
            value = default(T);

            var redisValue = _db.StringGet(key, flags);
            if (redisValue == RedisValue.Null)
                return false;

            if (typeof(T) == typeof(Int32))
                value = (T)Convert.ChangeType(redisValue, typeof(T));
            else if (typeof(T) == typeof(Int64))
                value = (T)Convert.ChangeType(redisValue, typeof(T));
            else if (typeof(T) == typeof(Int16))
                value = (T)Convert.ChangeType(redisValue, typeof(T));
            else if (typeof(T) == typeof(bool))
                value = (T)Convert.ChangeType(redisValue, typeof(T));
            else if (typeof(T) == typeof(double))
                value = (T)Convert.ChangeType(redisValue, typeof(T));
            else 
                value = _serializer.Deserialize<T>((string)redisValue);

            return true;
        }

        protected T ChangeType<T>(RedisValue value) {
            if (value == RedisValue.Null && IsNullable(typeof(T)))
                return default(T);

            if (typeof(T) == typeof(Int16)
                || typeof(T) == typeof(Int32)
                || typeof(T) == typeof(Int64)
                || typeof(T) == typeof(double)
                || typeof(T) == typeof(bool))
                return (T)Convert.ChangeType(value, typeof(T));

            return _serializer.Deserialize<T>((string)value);
        }

        private static bool IsNullable(Type type) {
            if (!type.IsValueType)
                return true; // ref-type
            if (Nullable.GetUnderlyingType(type) != null)
                return true; // Nullable<T>

            return false;
        }

        public long Increment(string key, uint amount) {
            return _db.StringIncrement(key, amount);
        }

        public long Increment(string key, uint amount, DateTime expiresAt) {
            return Increment(key, amount, expiresAt.ToUniversalTime().Subtract(DateTime.UtcNow));
        }

        public long Increment(string key, uint amount, TimeSpan expiresIn) {
            if (expiresIn.Ticks < 0) {
                Remove(key);
                return -1;
            }

            var result = _db.StringIncrement(key, amount);
            _db.KeyExpire(key, expiresIn);
            return result;
        }

        public long Decrement(string key, uint amount) {
            return _db.StringDecrement(key, amount);
        }

        public long Decrement(string key, uint amount, DateTime expiresAt) {
            return Decrement(key, amount, expiresAt.ToUniversalTime().Subtract(DateTime.UtcNow));
        }

        public long Decrement(string key, uint amount, TimeSpan expiresIn) {
            if (expiresIn.Ticks < 0) {
                Remove(key);
                return -1;
            }

            var result = _db.StringDecrement(key, amount);
            _db.KeyExpire(key, expiresIn);
            return result;
        }

        public bool Add<T>(string key, T value) {
            return InternalSet(key, value, null, When.NotExists);
        }

        public bool Set<T>(string key, T value) {
            return InternalSet(key, value);
        }

        public bool Replace<T>(string key, T value) {
            return InternalSet(key, value, null, When.Exists);
        }

        public bool Set<T>(string key, T value, DateTime expiresAt) {
            return Set(key, value, expiresAt.ToUniversalTime().Subtract(DateTime.UtcNow));
        }

        public bool Replace<T>(string key, T value, DateTime expiresAt) {
            return Replace(key, value, expiresAt.ToUniversalTime().Subtract(DateTime.UtcNow));
        }

        public bool Add<T>(string key, T value, DateTime expiresAt) {
            return Add(key, value, expiresAt.ToUniversalTime().Subtract(DateTime.UtcNow));
        }

        public bool Add<T>(string key, T value, TimeSpan expiresIn) {
            if (expiresIn.Ticks < 0) {
                Remove(key);
                return false;
            }

            return InternalSet(key, value, expiresIn, When.NotExists);
        }

        public bool Set<T>(string key, T value, TimeSpan expiresIn) {
            return InternalSet(key, value, expiresIn);
        }

        protected bool InternalSet<T>(string key, T value, TimeSpan? expiresIn = null, When when = When.Always, CommandFlags flags = CommandFlags.None) {
            if (typeof(T) == typeof(Int16))
                return _db.StringSet(key, Convert.ToInt16(value), expiresIn, when, flags);
            if (typeof(T) == typeof(Int32))
                return _db.StringSet(key, Convert.ToInt32(value), expiresIn, when, flags);
            if (typeof(T) == typeof(Int64))
                return _db.StringSet(key, Convert.ToInt64(value), expiresIn, when, flags);
            if (typeof(T) == typeof(bool))
                return _db.StringSet(key, Convert.ToBoolean(value), expiresIn, when, flags);

            var data = _serializer.Serialize(value);
            return _db.StringSet(key, data, expiresIn, when, flags);
        }

        public bool Replace<T>(string key, T value, TimeSpan expiresIn) {
            return InternalSet(key, value, expiresIn, When.Exists);
        }

        public void FlushAll() {
            var endpoints = _connectionMultiplexer.GetEndPoints(true);
            if (endpoints.Length == 0)
                return;

            foreach (var endpoint in endpoints) {
                var server = _connectionMultiplexer.GetServer(endpoint);
                
                try {
                    server.FlushDatabase();
                    continue;
                } catch (Exception) {}

                try {
                    var keys = server.Keys().ToArray();
                    if (keys.Length > 0)
                        _db.KeyDelete(keys);
                } catch (Exception) {}   
            }
        }

        public IDictionary<string, T> GetAll<T>(IEnumerable<string> keys) {
            var keyArray = keys.ToArray();
            var values = _db.StringGet(keyArray.Select(k => (RedisKey)k).ToArray());

            var result = new Dictionary<string, T>();
            for (int i = 0; i < keyArray.Length; i++) {
                T value = _serializer.Deserialize<T>((string)values[i]);
                result.Add(keyArray[i], value);
            }

            return result;
        }

        public void SetAll<T>(IDictionary<string, T> values) {
            if (values == null)
                return;

            _db.StringSet(values.ToDictionary(v => (RedisKey)v.Key, v => (RedisValue)_serializer.Serialize(v.Value)).ToArray());
        }

        public DateTime? GetExpiration(string key) {
            var expiration = _db.KeyTimeToLive(key);
            if (!expiration.HasValue)
                return null;

            return DateTime.UtcNow.Add(expiration.Value);
        }

        public void SetExpiration(string key, DateTime expiresAt) {
            SetExpiration(key, expiresAt.ToUniversalTime().Subtract(DateTime.UtcNow));
        }

        public void SetExpiration(string key, TimeSpan expiresIn) {
            if (expiresIn.Ticks < 0) {
                Remove(key);
                return;
            }

            _db.KeyExpire(key, expiresIn);
        }

        ISerializer IHaveSerializer.Serializer {
            get { return _serializer; }
        }

        public void Dispose() { }
    }
}