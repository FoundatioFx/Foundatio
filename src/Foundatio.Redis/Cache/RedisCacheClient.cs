using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Redis;
using Foundatio.Serializer;
using Foundatio.AsyncEx;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace Foundatio.Caching {
    public sealed class RedisCacheClient : ICacheClient, IHaveSerializer {
        private readonly RedisCacheClientOptions _options;
        private readonly ILogger _logger;

        private readonly AsyncLock _lock = new AsyncLock();
        private bool _scriptsLoaded;
        private LoadedLuaScript _setIfHigherScript;
        private LoadedLuaScript _setIfLowerScript;
        private LoadedLuaScript _incrByAndExpireScript;
        private LoadedLuaScript _delByWildcardScript;

        public RedisCacheClient(RedisCacheClientOptions options) {
            options.ConnectionMultiplexer.ConnectionRestored += ConnectionMultiplexerOnConnectionRestored;
            options.Serializer = options.Serializer ?? new JsonNetSerializer();
            _options = options;
            _logger = options.LoggerFactory?.CreateLogger(typeof(RedisCacheClient)) ?? NullLogger.Instance;
        }

        public async Task<int> RemoveAllAsync(IEnumerable<string> keys = null) {
            if (keys == null) {
                var endpoints = _options.ConnectionMultiplexer.GetEndPoints(true);
                if (endpoints.Length == 0)
                    return 0;

                foreach (var endpoint in endpoints) {
                    var server = _options.ConnectionMultiplexer.GetServer(endpoint);

                    try {
                        await server.FlushDatabaseAsync(Database.Database).AnyContext();
                        continue;
                    } catch (Exception) {}

                    try {
                        var redisKeys = server.Keys(Database.Database).ToArray();
                        if (redisKeys.Length > 0)
                            await Database.KeyDeleteAsync(redisKeys).AnyContext();
                    } catch (Exception) {}
                }
            } else {
                var redisKeys = keys.Where(k => !String.IsNullOrEmpty(k)).Select(k => (RedisKey)k).ToArray();
                if (redisKeys.Length > 0)
                    return (int)await Database.KeyDeleteAsync(redisKeys).AnyContext();
            }

            return 0;
        }

        public async Task<int> RemoveByPrefixAsync(string prefix) {
            await LoadScriptsAsync().AnyContext();

            try {
                var result = await Database.ScriptEvaluateAsync(_delByWildcardScript, new { keys = prefix + "*" }).AnyContext();
                return (int)result;
            } catch (RedisServerException) {
                return 0;
            }
        }

        private static readonly RedisValue _nullValue = "@@NULL";

        public async Task<CacheValue<T>> GetAsync<T>(string key) {
            if (String.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

            var redisValue = await Database.StringGetAsync(key).AnyContext();
            return RedisValueToCacheValue<T>(redisValue);
        }

        private  CacheValue<ICollection<T>> RedisValuesToCacheValue<T>(RedisValue[] redisValues) {
            var result = new List<T>();
            foreach (var redisValue in redisValues) {
                if (!redisValue.HasValue)
                    continue;
                if (redisValue == _nullValue)
                    continue;

                try {
                    var value = redisValue.ToValueOfType<T>(_options.Serializer);
                    result.Add(value);
                } catch (Exception ex) {
                    _logger.LogError(ex, "Unable to deserialize value \"{redisValue}\" to type {type}", redisValue, typeof(T).FullName);
                }
            }

            return new CacheValue<ICollection<T>>(result, true);
        }

        private CacheValue<T> RedisValueToCacheValue<T>(RedisValue redisValue) {
            if (!redisValue.HasValue) return CacheValue<T>.NoValue;
            if (redisValue == _nullValue) return CacheValue<T>.Null;

            try {
                var value = redisValue.ToValueOfType<T>(_options.Serializer);
                return new CacheValue<T>(value, true);
            } catch (Exception ex) {
                _logger.LogError(ex, "Unable to deserialize value \"{redisValue}\" to type {type}", redisValue, typeof(T).FullName);
                return CacheValue<T>.NoValue;
            }
        }

        public async Task<IDictionary<string, CacheValue<T>>> GetAllAsync<T>(IEnumerable<string> keys) {
            var keyArray = keys.ToArray();
            var values = await Database.StringGetAsync(keyArray.Select(k => (RedisKey)k).ToArray()).AnyContext();

            var result = new Dictionary<string, CacheValue<T>>();
            for (int i = 0; i < keyArray.Length; i++)
                result.Add(keyArray[i], RedisValueToCacheValue<T>(values[i]));

            return result;
        }

        public async Task<CacheValue<ICollection<T>>> GetSetAsync<T>(string key) {
            if (String.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

            var set = await Database.SetMembersAsync(key).AnyContext();
            return RedisValuesToCacheValue<T>(set);
        }

        public async Task<bool> AddAsync<T>(string key, T value, TimeSpan? expiresIn = null) {
            if (String.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

            if (expiresIn?.Ticks < 0) {
                _logger.LogTrace("Removing expired key: {key}", key);

                await this.RemoveAsync(key).AnyContext();
                return false;
            }

            return await InternalSetAsync(key, value, expiresIn, When.NotExists).AnyContext();
        }

        public async Task<long> SetAddAsync<T>(string key, IEnumerable<T> values, TimeSpan? expiresIn = null) {
            if (String.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

            if (values == null)
                throw new ArgumentNullException(nameof(values));

            if (expiresIn?.Ticks < 0) {
                _logger.LogTrace("Removing expired key: {key}", key);

                await this.RemoveAsync(key).AnyContext();
                return default(long);
            }

            var redisValues = new List<RedisValue>();
            foreach (var value in values.Distinct())
                redisValues.Add(value.ToRedisValue(_options.Serializer));

            long result = await Database.SetAddAsync(key, redisValues.ToArray()).AnyContext();
            if (result > 0 && expiresIn.HasValue)
                await SetExpirationAsync(key, expiresIn.Value).AnyContext();

            return result;
        }

        public async Task<long> SetRemoveAsync<T>(string key, IEnumerable<T> values, TimeSpan? expiresIn = null) {
            if (String.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

            if (values == null)
                throw new ArgumentNullException(nameof(values));

            if (expiresIn?.Ticks < 0) {
                _logger.LogTrace("Removing expired key: {key}", key);

                await this.RemoveAsync(key).AnyContext();
                return default(long);
            }

            var redisValues = new List<RedisValue>();
            foreach (var value in values.Distinct())
                redisValues.Add(value.ToRedisValue(_options.Serializer));

            long result = await Database.SetRemoveAsync(key, redisValues.ToArray()).AnyContext();
            if (result > 0 && expiresIn.HasValue)
                await SetExpirationAsync(key, expiresIn.Value).AnyContext();

            return result;
        }

        public Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiresIn = null) {
            if (String.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

            return InternalSetAsync(key, value, expiresIn);
        }

        public async Task<double> SetIfHigherAsync(string key, double value, TimeSpan? expiresIn = null) {
            if (String.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

            await LoadScriptsAsync().AnyContext();

            var result = await Database.ScriptEvaluateAsync(_setIfHigherScript, new { key, value, expires = expiresIn?.TotalSeconds }).AnyContext();
            return (double)result;
        }

        public async Task<double> SetIfLowerAsync(string key, double value, TimeSpan? expiresIn = null) {
            if (String.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

            await LoadScriptsAsync().AnyContext();

            var result = await Database.ScriptEvaluateAsync(_setIfLowerScript, new { key, value, expires = expiresIn?.TotalSeconds }).AnyContext();
            return (double)result;
        }

        private Task<bool> InternalSetAsync<T>(string key, T value, TimeSpan? expiresIn = null, When when = When.Always, CommandFlags flags = CommandFlags.None) {
            var redisValue = value.ToRedisValue(_options.Serializer);
            return Database.StringSetAsync(key, redisValue, expiresIn, when, flags);
        }

        public async Task<int> SetAllAsync<T>(IDictionary<string, T> values, TimeSpan? expiresIn = null) {
            if (values == null || values.Count == 0)
                return 0;

            var tasks = new List<Task<bool>>();
            foreach (var pair in values)
                tasks.Add(Database.StringSetAsync(pair.Key, pair.Value.ToRedisValue(_options.Serializer), expiresIn));

            var results = await Task.WhenAll(tasks).AnyContext();
            return results.Count(r => r);
        }

        public Task<bool> ReplaceAsync<T>(string key, T value, TimeSpan? expiresIn = null) {
            if (String.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

            return InternalSetAsync(key, value, expiresIn, When.Exists);
        }

        public async Task<double> IncrementAsync(string key, double amount = 1, TimeSpan? expiresIn = null) {
            if (String.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

            if (expiresIn?.Ticks < 0) {
                await this.RemoveAsync(key).AnyContext();
                return -1;
            }

            if (expiresIn.HasValue) {
                await LoadScriptsAsync().AnyContext();
                var result = await Database.ScriptEvaluateAsync(_incrByAndExpireScript, new { key, value = amount, expires = expiresIn.Value.TotalSeconds }).AnyContext();
                return (long)result;
            }

            return await Database.StringIncrementAsync(key, amount).AnyContext();
        }

        public Task<bool> ExistsAsync(string key) {
            if (String.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

            return Database.KeyExistsAsync(key);
        }

        public Task<TimeSpan?> GetExpirationAsync(string key) {
            if (String.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

            return Database.KeyTimeToLiveAsync(key);
        }

        public Task SetExpirationAsync(string key, TimeSpan expiresIn) {
            if (String.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

            if (expiresIn.Ticks < 0)
                return this.RemoveAsync(key);

            return Database.KeyExpireAsync(key, expiresIn);
        }

        private IDatabase Database => _options.ConnectionMultiplexer.GetDatabase();

        private async Task LoadScriptsAsync() {
            if (_scriptsLoaded)
                return;

            using (await _lock.LockAsync().AnyContext()) {
                if (_scriptsLoaded)
                    return;

                var setIfLower = LuaScript.Prepare(SET_IF_LOWER);
                var setIfHigher = LuaScript.Prepare(SET_IF_HIGHER);
                var incrByAndExpire = LuaScript.Prepare(INCRBY_AND_EXPIRE);
                var delByWildcard = LuaScript.Prepare(DEL_BY_WILDCARD);

                foreach (var endpoint in _options.ConnectionMultiplexer.GetEndPoints()) {
                    var server = _options.ConnectionMultiplexer.GetServer(endpoint);
                    _setIfHigherScript = await setIfHigher.LoadAsync(server).AnyContext();
                    _setIfLowerScript = await setIfLower.LoadAsync(server).AnyContext();
                    _incrByAndExpireScript = await incrByAndExpire.LoadAsync(server).AnyContext();
                    _delByWildcardScript = await delByWildcard.LoadAsync(server).AnyContext();
                }

                _scriptsLoaded = true;
            }
        }

        private void ConnectionMultiplexerOnConnectionRestored(object sender, ConnectionFailedEventArgs connectionFailedEventArgs) {
            _logger.LogInformation("Redis connection restored.");
            _scriptsLoaded = false;
        }

        public void Dispose() {
            _options.ConnectionMultiplexer.ConnectionRestored -= ConnectionMultiplexerOnConnectionRestored;
        }

        ISerializer IHaveSerializer.Serializer => _options.Serializer;

        private const string SET_IF_HIGHER = @"local c = tonumber(redis.call('get', @key))
if c then
  if tonumber(@value) > c then
    redis.call('set', @key, @value)
    if (@expires) then
      redis.call('expire', @key, math.ceil(@expires))
    end
    return tonumber(@value) - c
  else
    return 0
  end
else
  redis.call('set', @key, @value)
  if (@expires) then
    redis.call('expire', @key, math.ceil(@expires))
  end
  return tonumber(@value)
end";

        private const string SET_IF_LOWER = @"local c = tonumber(redis.call('get', @key))
if c then
  if tonumber(@value) > c then
    redis.call('set', @key, @value)
    if (@expires) then
      redis.call('expire', @key, math.ceil(@expires))
    end
    return tonumber(@value) - c
  else
    return 0
  end
else
  redis.call('set', @key, @value)
  if (@expires) then
    redis.call('expire', @key, math.ceil(@expires))
  end
  return tonumber(@value)
end";

        private const string INCRBY_AND_EXPIRE = @"if math.modf(@value) == 0 then
  local v = redis.call('incrby', @key, @value)
  if (@expires) then
    redis.call('expire', @key, math.ceil(@expires))
  end
  return tonumber(v)
else
  local v = redis.call('incrbyfloat', @key, @value)
  if (@expires) then
    redis.call('expire', @key, math.ceil(@expires))
  end
  return tonumber(v)
end";

        private const string DEL_BY_WILDCARD = @"local remove = redis.call('keys', @keys)
if unpack(remove) ~= nil then
  return redis.call('del', unpack(remove))
else
  return 0
end";
    }
}
