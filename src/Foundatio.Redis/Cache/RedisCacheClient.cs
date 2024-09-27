using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Foundatio.AsyncEx;
using Foundatio.Extensions;
using Foundatio.Redis;
using Foundatio.Redis.Utility;
using Foundatio.Serializer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace Foundatio.Caching;

public sealed class RedisCacheClient : ICacheClient, IHaveSerializer
{
    private readonly RedisCacheClientOptions _options;
    private readonly ILogger _logger;

    private readonly AsyncLock _lock = new();
    private bool _scriptsLoaded;

    private LoadedLuaScript _incrementWithExpire;
    private LoadedLuaScript _removeIfEqual;
    private LoadedLuaScript _replaceIfEqual;
    private LoadedLuaScript _setIfHigher;
    private LoadedLuaScript _setIfLower;

    public RedisCacheClient(RedisCacheClientOptions options)
    {
        _options = options;
        options.Serializer = options.Serializer ?? DefaultSerializer.Instance;
        _logger = options.LoggerFactory?.CreateLogger(typeof(RedisCacheClient)) ?? NullLogger.Instance;
        options.ConnectionMultiplexer.ConnectionRestored += ConnectionMultiplexerOnConnectionRestored;
    }

    public RedisCacheClient(Builder<RedisCacheClientOptionsBuilder, RedisCacheClientOptions> config)
        : this(config(new RedisCacheClientOptionsBuilder()).Build()) { }

    public IDatabase Database => _options.ConnectionMultiplexer.GetDatabase();

    public Task<bool> RemoveAsync(string key)
    {
        return Database.KeyDeleteAsync(key);
    }

    public async Task<bool> RemoveIfEqualAsync<T>(string key, T expected)
    {
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

        await LoadScriptsAsync().AnyContext();

        var expectedValue = expected.ToRedisValue(_options.Serializer);
        var redisResult = await Database.ScriptEvaluateAsync(_removeIfEqual, new { key = (RedisKey)key, expected = expectedValue }).AnyContext();
        var result = (int)redisResult;

        return result > 0;
    }

    public async Task<int> RemoveAllAsync(IEnumerable<string> keys = null)
    {
        if (keys == null)
        {
            var endpoints = _options.ConnectionMultiplexer.GetEndPoints();
            if (endpoints.Length == 0)
                return 0;

            foreach (var endpoint in endpoints)
            {
                var server = _options.ConnectionMultiplexer.GetServer(endpoint);
                if (server.IsReplica)
                    continue;

                try
                {
                    await server.FlushDatabaseAsync().AnyContext();
                    continue;
                }
                catch (Exception) { }

                try
                {
                    await foreach (var key in server.KeysAsync().ConfigureAwait(false))
                        await Database.KeyDeleteAsync(key).AnyContext();
                }
                catch (Exception) { }
            }
        }
        else
        {
            var redisKeys = keys.Where(k => !String.IsNullOrEmpty(k)).Select(k => (RedisKey)k).ToArray();
            if (redisKeys.Length > 0)
                return (int)await Database.KeyDeleteAsync(redisKeys).AnyContext();
        }

        return 0;
    }

    public async Task<int> RemoveByPrefixAsync(string prefix)
    {
        const int chunkSize = 2500;
        string normalizedPrefix = String.IsNullOrWhiteSpace(prefix) ? "*" : prefix.Trim();
        string regex = normalizedPrefix.Contains("*") ? normalizedPrefix : $"{normalizedPrefix}*";

        int total = 0;
        int index = 0;

        (int cursor, string[] keys) = await ScanKeysAsync(regex, index, chunkSize).AnyContext();

        while (keys.Length != 0 || index < chunkSize)
        {
            total += await RemoveAllAsync(keys).AnyContext();
            index += chunkSize;
            (cursor, keys) = await ScanKeysAsync(regex, cursor, chunkSize).AnyContext();
        }

        return total;
    }

    /// <summary>
    /// Scan for keys matching the prefix
    /// </summary>
    /// <remarks>SCAN, SSCAN, HSCAN and ZSCAN return a two elements multi-bulk reply, where the first element
    /// is a string representing an unsigned 64 bit number (the cursor), and the second element is a multi-bulk
    /// with an array of elements.</remarks>
    private async Task<(int, string[])> ScanKeysAsync(string prefix, int index, int chunkSize)
    {
        var result = await Database.ExecuteAsync("scan", index, "match", prefix, "count", chunkSize).AnyContext();
        var value = (RedisResult[])result;
        return ((int)value![0], (string[])value[1]);
    }

    private static readonly RedisValue _nullValue = "@@NULL";

    public async Task<CacheValue<T>> GetAsync<T>(string key)
    {
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

        var redisValue = await Database.StringGetAsync(key, _options.ReadMode).AnyContext();
        return RedisValueToCacheValue<T>(redisValue);
    }

    private CacheValue<ICollection<T>> RedisValuesToCacheValue<T>(RedisValue[] redisValues)
    {
        var result = new List<T>();
        foreach (var redisValue in redisValues)
        {
            if (!redisValue.HasValue)
                continue;
            if (redisValue == _nullValue)
                continue;

            try
            {
                var value = redisValue.ToValueOfType<T>(_options.Serializer);
                result.Add(value);
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(LogLevel.Error))
                    _logger.LogError(ex, "Unable to deserialize value {Value} to type {Type}", redisValue, typeof(T).FullName);

                if (_options.ShouldThrowOnSerializationError)
                    throw;
            }
        }

        return new CacheValue<ICollection<T>>(result, true);
    }

    private CacheValue<T> RedisValueToCacheValue<T>(RedisValue redisValue)
    {
        if (!redisValue.HasValue) return CacheValue<T>.NoValue;
        if (redisValue == _nullValue) return CacheValue<T>.Null;

        try
        {
            var value = redisValue.ToValueOfType<T>(_options.Serializer);
            return new CacheValue<T>(value, true);
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Error))
                _logger.LogError(ex, "Unable to deserialize value {Value} to type {Type}", redisValue, typeof(T).FullName);

            if (_options.ShouldThrowOnSerializationError)
                throw;

            return CacheValue<T>.NoValue;
        }
    }

    public async Task<IDictionary<string, CacheValue<T>>> GetAllAsync<T>(IEnumerable<string> keys)
    {
        string[] keyArray = keys.ToArray();
        var values = await Database.StringGetAsync(keyArray.Select(k => (RedisKey)k).ToArray(), _options.ReadMode).AnyContext();

        var result = new Dictionary<string, CacheValue<T>>();
        for (int i = 0; i < keyArray.Length; i++)
            result.Add(keyArray[i], RedisValueToCacheValue<T>(values[i]));

        return result;
    }

    public async Task<CacheValue<ICollection<T>>> GetListAsync<T>(string key, int? page = null, int pageSize = 100)
    {
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

        if (page.HasValue && page.Value < 1)
            throw new ArgumentNullException(nameof(page), "Page cannot be less than 1.");

        if (!page.HasValue)
        {
            var set = await Database.SortedSetRangeByScoreAsync(key, flags: _options.ReadMode).AnyContext();
            return RedisValuesToCacheValue<T>(set);
        }
        else
        {
            long start = ((page.Value - 1) * pageSize);
            long end = start + pageSize - 1;
            var set = await Database.SortedSetRangeByRankAsync(key, start, end, flags: _options.ReadMode).AnyContext();
            return RedisValuesToCacheValue<T>(set);
        }
    }

    public async Task<bool> AddAsync<T>(string key, T value, TimeSpan? expiresIn = null)
    {
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

        if (expiresIn?.Ticks < 0)
        {
            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Removing expired key: {Key}", key);

            await RemoveAsync(key).AnyContext();
            return false;
        }

        return await InternalSetAsync(key, value, expiresIn, When.NotExists).AnyContext();
    }

    public async Task<long> ListAddAsync<T>(string key, IEnumerable<T> values, TimeSpan? expiresIn = null)
    {
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

        if (values == null)
            throw new ArgumentNullException(nameof(values));

        if (expiresIn?.Ticks < 0)
        {
            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Removing expired key: {Key}", key);

            await RemoveAsync(key).AnyContext();
            return default;
        }

        long highestScore = 0;
        try
        {
            var items = await Database.SortedSetRangeByRankWithScoresAsync(key, 0, 0, order: Order.Descending);
            highestScore = items.Length > 0 ? (long)items.First().Score : 0;
        }
        catch (RedisServerException ex) when (ex.Message.StartsWith("WRONGTYPE"))
        {
            // convert legacy set to sortedset
            var oldItems = await Database.SetMembersAsync(key).AnyContext();
            await Database.KeyDeleteAsync(key).AnyContext();

            if (values is string)
            {
                var oldItemValues = new List<string>();
                foreach (var oldItem in RedisValuesToCacheValue<string>(oldItems).Value)
                    oldItemValues.Add(oldItem);

                highestScore = await ListAddAsync(key, oldItemValues).AnyContext();
            }
            else
            {
                var oldItemValues = new List<T>();
                foreach (var oldItem in RedisValuesToCacheValue<T>(oldItems).Value)
                    oldItemValues.Add(oldItem);

                highestScore = await ListAddAsync(key, oldItemValues).AnyContext();
            }
        }

        var redisValues = new List<SortedSetEntry>();
        if (values is string stringValue)
        {
            redisValues.Add(new SortedSetEntry(stringValue.ToRedisValue(_options.Serializer), highestScore + 1));
        }
        else
        {
            var valuesArray = values.ToArray();
            for (int i = 0; i < valuesArray.Length; i++)
                redisValues.Add(new SortedSetEntry(valuesArray[i].ToRedisValue(_options.Serializer), highestScore + i + 1));
        }

        long result = await Database.SortedSetAddAsync(key, redisValues.ToArray()).AnyContext();
        if (result > 0 && expiresIn.HasValue)
            await SetExpirationAsync(key, expiresIn.Value).AnyContext();

        return result;
    }

    public async Task<long> ListRemoveAsync<T>(string key, IEnumerable<T> values, TimeSpan? expiresIn = null)
    {
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

        if (values == null)
            throw new ArgumentNullException(nameof(values));

        if (expiresIn?.Ticks < 0)
        {
            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Removing expired key: {Key}", key);

            await RemoveAsync(key).AnyContext();
            return default;
        }

        var redisValues = new List<RedisValue>();
        if (values is string stringValue)
            redisValues.Add(stringValue.ToRedisValue(_options.Serializer));
        else
            foreach (var value in values)
                redisValues.Add(value.ToRedisValue(_options.Serializer));

        try
        {
            long result = await Database.SortedSetRemoveAsync(key, redisValues.ToArray()).AnyContext();
            if (result > 0 && expiresIn.HasValue)
                await SetExpirationAsync(key, expiresIn.Value).AnyContext();

            return result;
        }
        catch (RedisServerException ex) when (ex.Message.StartsWith("WRONGTYPE"))
        {
            // convert legacy set to sortedset
            var oldItems = await Database.SetMembersAsync(key).AnyContext();
            await Database.KeyDeleteAsync(key).AnyContext();

            if (values is string)
            {
                var oldItemValues = new List<string>();
                foreach (var oldItem in RedisValuesToCacheValue<string>(oldItems).Value)
                    oldItemValues.Add(oldItem);

                await ListAddAsync(key, oldItemValues).AnyContext();
            }
            else
            {
                var oldItemValues = new List<T>();
                foreach (var oldItem in RedisValuesToCacheValue<T>(oldItems).Value)
                    oldItemValues.Add(oldItem);

                await ListAddAsync(key, oldItemValues).AnyContext();
            }

            // try again
            return await ListRemoveAsync(key, values).AnyContext();
        }
    }

    public Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiresIn = null)
    {
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

        return InternalSetAsync(key, value, expiresIn);
    }

    public async Task<double> SetIfHigherAsync(string key, double value, TimeSpan? expiresIn = null)
    {
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

        await LoadScriptsAsync().AnyContext();

        if (expiresIn.HasValue)
        {
            var result = await Database.ScriptEvaluateAsync(_setIfHigher, new { key = (RedisKey)key, value, expires = (int)expiresIn.Value.TotalMilliseconds }).AnyContext();
            return (double)result;
        }
        else
        {
            var result = await Database.ScriptEvaluateAsync(_setIfHigher, new { key = (RedisKey)key, value, expires = RedisValue.EmptyString }).AnyContext();
            return (double)result;
        }
    }

    public async Task<long> SetIfHigherAsync(string key, long value, TimeSpan? expiresIn = null)
    {
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

        await LoadScriptsAsync().AnyContext();

        if (expiresIn.HasValue)
        {
            var result = await Database.ScriptEvaluateAsync(_setIfHigher, new { key = (RedisKey)key, value, expires = (int)expiresIn.Value.TotalMilliseconds }).AnyContext();
            return (long)result;
        }
        else
        {
            var result = await Database.ScriptEvaluateAsync(_setIfHigher, new { key = (RedisKey)key, value, expires = RedisValue.EmptyString }).AnyContext();
            return (long)result;
        }
    }

    public async Task<double> SetIfLowerAsync(string key, double value, TimeSpan? expiresIn = null)
    {
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

        await LoadScriptsAsync().AnyContext();

        if (expiresIn.HasValue)
        {
            var result = await Database.ScriptEvaluateAsync(_setIfLower, new { key = (RedisKey)key, value, expires = (int)expiresIn.Value.TotalMilliseconds }).AnyContext();
            return (double)result;
        }
        else
        {
            var result = await Database.ScriptEvaluateAsync(_setIfLower, new { key = (RedisKey)key, value, expires = RedisValue.EmptyString }).AnyContext();
            return (double)result;
        }
    }

    public async Task<long> SetIfLowerAsync(string key, long value, TimeSpan? expiresIn = null)
    {
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

        await LoadScriptsAsync().AnyContext();

        if (expiresIn.HasValue)
        {
            var result = await Database.ScriptEvaluateAsync(_setIfLower, new { key = (RedisKey)key, value, expires = (int)expiresIn.Value.TotalMilliseconds }).AnyContext();
            return (long)result;
        }
        else
        {
            var result = await Database.ScriptEvaluateAsync(_setIfLower, new { key = (RedisKey)key, value, expires = RedisValue.EmptyString }).AnyContext();
            return (long)result;
        }
    }

    private Task<bool> InternalSetAsync<T>(string key, T value, TimeSpan? expiresIn = null, When when = When.Always, CommandFlags flags = CommandFlags.None)
    {
        var redisValue = value.ToRedisValue(_options.Serializer);
        return Database.StringSetAsync(key, redisValue, expiresIn, when, flags);
    }

    public async Task<int> SetAllAsync<T>(IDictionary<string, T> values, TimeSpan? expiresIn = null)
    {
        if (values == null || values.Count == 0)
            return 0;

        var tasks = new List<Task<bool>>();
        foreach (var pair in values)
            tasks.Add(Database.StringSetAsync(pair.Key, pair.Value.ToRedisValue(_options.Serializer), expiresIn));

        bool[] results = await Task.WhenAll(tasks).AnyContext();
        return results.Count(r => r);
    }

    public Task<bool> ReplaceAsync<T>(string key, T value, TimeSpan? expiresIn = null)
    {
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

        return InternalSetAsync(key, value, expiresIn, When.Exists);
    }

    public async Task<bool> ReplaceIfEqualAsync<T>(string key, T value, T expected, TimeSpan? expiresIn = null)
    {
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

        await LoadScriptsAsync().AnyContext();

        var redisValue = value.ToRedisValue(_options.Serializer);
        var expectedValue = expected.ToRedisValue(_options.Serializer);
        RedisResult redisResult;
        if (expiresIn.HasValue)
            redisResult = await Database.ScriptEvaluateAsync(_replaceIfEqual, new { key = (RedisKey)key, value = redisValue, expected = expectedValue, expires = (int)expiresIn.Value.TotalMilliseconds }).AnyContext();
        else
            redisResult = await Database.ScriptEvaluateAsync(_replaceIfEqual, new { key = (RedisKey)key, value = redisValue, expected = expectedValue, expires = "" }).AnyContext();

        var result = (int)redisResult;

        return result > 0;
    }

    public async Task<double> IncrementAsync(string key, double amount = 1, TimeSpan? expiresIn = null)
    {
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

        if (expiresIn?.Ticks < 0)
        {
            await RemoveAsync(key).AnyContext();
            return -1;
        }

        if (expiresIn.HasValue)
        {
            await LoadScriptsAsync().AnyContext();
            var result = await Database.ScriptEvaluateAsync(_incrementWithExpire, new { key = (RedisKey)key, value = amount, expires = (int)expiresIn.Value.TotalMilliseconds }).AnyContext();
            return (double)result;
        }

        return await Database.StringIncrementAsync(key, amount).AnyContext();
    }

    public async Task<long> IncrementAsync(string key, long amount = 1, TimeSpan? expiresIn = null)
    {
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

        if (expiresIn?.Ticks < 0)
        {
            await RemoveAsync(key).AnyContext();
            return -1;
        }

        if (expiresIn.HasValue)
        {
            await LoadScriptsAsync().AnyContext();
            var result = await Database.ScriptEvaluateAsync(_incrementWithExpire, new { key = (RedisKey)key, value = amount, expires = (int)expiresIn.Value.TotalMilliseconds }).AnyContext();
            return (long)result;
        }

        return await Database.StringIncrementAsync(key, amount).AnyContext();
    }

    public Task<bool> ExistsAsync(string key)
    {
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

        return Database.KeyExistsAsync(key);
    }

    public Task<TimeSpan?> GetExpirationAsync(string key)
    {
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

        return Database.KeyTimeToLiveAsync(key);
    }

    public Task SetExpirationAsync(string key, TimeSpan expiresIn)
    {
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty.");

        if (expiresIn.Ticks < 0)
            return RemoveAsync(key);

        return Database.KeyExpireAsync(key, expiresIn);
    }

    private async Task LoadScriptsAsync()
    {
        if (_scriptsLoaded)
            return;

        using (await _lock.LockAsync().AnyContext())
        {
            if (_scriptsLoaded)
                return;

            var incrementWithExpire = LuaScript.Prepare(IncrementWithScript);
            var removeIfEqual = LuaScript.Prepare(RemoveIfEqualScript);
            var replaceIfEqual = LuaScript.Prepare(ReplaceIfEqualScript);
            var setIfHigher = LuaScript.Prepare(SetIfHigherScript);
            var setIfLower = LuaScript.Prepare(SetIfLowerScript);

            foreach (var endpoint in _options.ConnectionMultiplexer.GetEndPoints())
            {
                var server = _options.ConnectionMultiplexer.GetServer(endpoint);
                if (server.IsReplica)
                    continue;

                _incrementWithExpire = await incrementWithExpire.LoadAsync(server).AnyContext();
                _removeIfEqual = await removeIfEqual.LoadAsync(server).AnyContext();
                _replaceIfEqual = await replaceIfEqual.LoadAsync(server).AnyContext();
                _setIfHigher = await setIfHigher.LoadAsync(server).AnyContext();
                _setIfLower = await setIfLower.LoadAsync(server).AnyContext();
            }

            _scriptsLoaded = true;
        }
    }

    private void ConnectionMultiplexerOnConnectionRestored(object sender, ConnectionFailedEventArgs connectionFailedEventArgs)
    {
        if (_logger.IsEnabled(LogLevel.Information)) _logger.LogInformation("Redis connection restored.");
        _scriptsLoaded = false;
    }

    public void Dispose()
    {
        _options.ConnectionMultiplexer.ConnectionRestored -= ConnectionMultiplexerOnConnectionRestored;
    }

    ISerializer IHaveSerializer.Serializer => _options.Serializer;

    private static readonly string IncrementWithScript = EmbeddedResourceLoader.GetEmbeddedResource("Foundatio.Redis.Scripts.IncrementWithExpire.lua");
    private static readonly string RemoveIfEqualScript = EmbeddedResourceLoader.GetEmbeddedResource("Foundatio.Redis.Scripts.RemoveIfEqual.lua");
    private static readonly string ReplaceIfEqualScript = EmbeddedResourceLoader.GetEmbeddedResource("Foundatio.Redis.Scripts.ReplaceIfEqual.lua");
    private static readonly string SetIfHigherScript = EmbeddedResourceLoader.GetEmbeddedResource("Foundatio.Redis.Scripts.SetIfHigher.lua");
    private static readonly string SetIfLowerScript = EmbeddedResourceLoader.GetEmbeddedResource("Foundatio.Redis.Scripts.SetIfLower.lua");
}
