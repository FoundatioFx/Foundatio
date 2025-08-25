using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Foundatio.AsyncEx;
using Foundatio.Extensions;
using Foundatio.Redis;
using Foundatio.Redis.Extensions;
using Foundatio.Redis.Utility;
using Foundatio.Serializer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace Foundatio.Caching;

public sealed class RedisCacheClient : ICacheClient, IHaveSerializer
{
    private readonly RedisCacheClientOptions _options;
    private readonly TimeProvider _timeProvider;
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
        _timeProvider = options.TimeProvider ?? TimeProvider.System;
        options.Serializer ??= DefaultSerializer.Instance;
        _logger = options.LoggerFactory?.CreateLogger(typeof(RedisCacheClient)) ?? NullLogger.Instance;

        options.ConnectionMultiplexer.ConnectionRestored += ConnectionMultiplexerOnConnectionRestored;
    }

    public RedisCacheClient(Builder<RedisCacheClientOptionsBuilder, RedisCacheClientOptions> config)
        : this(config(new RedisCacheClientOptionsBuilder()).Build())
    {
    }

    public IDatabase Database => _options.ConnectionMultiplexer.GetDatabase(_options.Database);

    public Task<bool> RemoveAsync(string key)
    {
        return Database.KeyDeleteAsync(key);
    }

    public async Task<bool> RemoveIfEqualAsync<T>(string key, T expected)
    {
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty");

        await LoadScriptsAsync().AnyContext();

        var expectedValue = expected.ToRedisValue(_options.Serializer);
        var redisResult = await Database.ScriptEvaluateAsync(_removeIfEqual, new { key = (RedisKey)key, expected = expectedValue }).AnyContext();
        int result = (int)redisResult;

        return result > 0;
    }

    public async Task<int> RemoveAllAsync(IEnumerable<string> keys = null)
    {
        // NOTE: Batch size matches default page size (RedisBase.CursorUtils.DefaultLibraryPageSize)
        int batchSize = 250;

        long deleted = 0;
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
                    long dbSize = await server.DatabaseSizeAsync(_options.Database).AnyContext();
                    await server.FlushDatabaseAsync(_options.Database).AnyContext();
                    deleted += dbSize;
                    continue;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unable to flush database: {Message}", ex.Message);
                }

                try
                {
                    // NOTE: We need to use a HashSet to avoid duplicate counts due to SCAN is non-deterministic.
                    // A Performance win could be had if we are sure dbSize didn't fail and we know nothing was changing
                    // keys while we were deleting.
                    var seen = new HashSet<RedisKey>();
                    await foreach (var key in server.KeysAsync(_options.Database).ConfigureAwait(false))
                        seen.Add(key);

                    foreach (var batch in seen.Batch(batchSize))
                        deleted += await Database.KeyDeleteAsync(batch.ToArray()).AnyContext();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error deleting all keys: {Message}", ex.Message);
                }
            }
        }
        else if (Database.Multiplexer.IsCluster())
        {
            foreach (var redisKeys in keys.Where(k => !String.IsNullOrEmpty(k)).Select(k => (RedisKey)k).Batch(batchSize))
            {
                foreach (var hashSlotGroup in redisKeys.GroupBy(k => Database.Multiplexer.HashSlot(k)))
                {
                    var hashSlotKeys = hashSlotGroup.ToArray();
                    try
                    {
                        deleted += await Database.KeyDeleteAsync(hashSlotKeys).AnyContext();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Unable to delete {HashSlot} keys ({Keys}): {Message}", hashSlotGroup.Key, hashSlotKeys, ex.Message);
                    }
                }
            }
        }
        else
        {
            foreach (var redisKeys in keys.Where(k => !String.IsNullOrEmpty(k)).Select(k => (RedisKey)k).Batch(batchSize))
            {
                try
                {
                    deleted += await Database.KeyDeleteAsync(redisKeys).AnyContext();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unable to delete keys ({Keys}): {Message}", redisKeys, ex.Message);
                }
            }
        }

        return (int)deleted;
    }

    public async Task<int> RemoveByPrefixAsync(string prefix)
    {
        if (String.IsNullOrEmpty(prefix))
            return await RemoveAllAsync().AnyContext();

        var endpoints = _options.ConnectionMultiplexer.GetEndPoints();
        if (endpoints.Length == 0)
            return 0;

        const int batchSize = 250;
        bool isCluster = _options.ConnectionMultiplexer.IsCluster();

        long deleted = 0;
        foreach (var endpoint in endpoints)
        {
            var server = _options.ConnectionMultiplexer.GetServer(endpoint);
            if (server.IsReplica)
                continue;

            await foreach (var keys in server.KeysAsync(
                               database: Database.Database,
                               pattern: $"{RedisPattern.Escape(prefix)}*",
                               pageSize: batchSize
                           ).BatchAsync(batchSize).ConfigureAwait(false))
            {
                if (isCluster)
                {
                    foreach (var slotGroup in keys.GroupBy(k => _options.ConnectionMultiplexer.HashSlot(k)))
                        deleted += await Database.KeyDeleteAsync(slotGroup.ToArray()).AnyContext();
                }
                else
                {
                    deleted += await Database.KeyDeleteAsync(keys).AnyContext();
                }
            }
        }

        return (int)deleted;
    }

    private static readonly RedisValue _nullValue = "@@NULL";

    public async Task<CacheValue<T>> GetAsync<T>(string key)
    {
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty");

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
                _logger.LogError(ex, "Unable to deserialize value {Value} to type {Type}: {Message}", redisValue, typeof(T).FullName, ex.Message);
                if (_options.ShouldThrowOnSerializationError)
                    throw;
            }
        }

        return new CacheValue<ICollection<T>>(result, result.Count > 0);
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
            _logger.LogError(ex, "Unable to deserialize value {Value} to type {Type}: {Message}", redisValue, typeof(T).FullName, ex.Message);
            if (_options.ShouldThrowOnSerializationError)
                throw;

            return CacheValue<T>.NoValue;
        }
    }

    public async Task<IDictionary<string, CacheValue<T>>> GetAllAsync<T>(IEnumerable<string> keys)
    {
        if (keys is null)
            throw new ArgumentNullException(nameof(keys));

        var redisKeys = keys.Distinct().Select(k => (RedisKey)k).ToArray();
        var result = new Dictionary<string, CacheValue<T>>(redisKeys.Length);
        if (redisKeys.Length == 0)
            return result;

        if (_options.ConnectionMultiplexer.IsCluster())
        {
            foreach (var hashSlotGroup in redisKeys.GroupBy(k => _options.ConnectionMultiplexer.HashSlot(k)))
            {
                var hashSlotKeys = hashSlotGroup.ToArray();
                var values = await Database.StringGetAsync(hashSlotKeys, _options.ReadMode).AnyContext();
                for (int i = 0; i < hashSlotKeys.Length; i++)
                    result[hashSlotKeys[i]] = RedisValueToCacheValue<T>(values[i]);
            }
        }
        else
        {
            var values = await Database.StringGetAsync(redisKeys, _options.ReadMode).AnyContext();
            for (int i = 0; i < redisKeys.Length; i++)
                result[redisKeys[i]] = RedisValueToCacheValue<T>(values[i]);
        }

        return result;
    }

    public async Task<CacheValue<ICollection<T>>> GetListAsync<T>(string key, int? page = null, int pageSize = 100)
    {
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty");

        if (page is < 1)
            throw new ArgumentOutOfRangeException(nameof(page), "Page cannot be less than 1");

        await RemoveExpiredListValuesAsync<T>(key, typeof(T) == typeof(string)).AnyContext();

        if (!page.HasValue)
        {
            var set = await Database.SortedSetRangeByScoreAsync(key, flags: _options.ReadMode).AnyContext();
            return RedisValuesToCacheValue<T>(set);
        }
        else
        {
            long start = (page.Value - 1) * pageSize;
            long end = start + pageSize - 1;
            var set = await Database.SortedSetRangeByRankAsync(key, start, end, flags: _options.ReadMode).AnyContext();
            return RedisValuesToCacheValue<T>(set);
        }
    }

    public Task<bool> AddAsync<T>(string key, T value, TimeSpan? expiresIn = null)
    {
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty");

        return InternalSetAsync(key, value, expiresIn, When.NotExists);
    }

    public async Task<long> ListAddAsync<T>(string key, IEnumerable<T> values, TimeSpan? expiresIn = null)
    {
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty");

        if (values == null)
            throw new ArgumentNullException(nameof(values));

        var expiresAt = expiresIn.HasValue ? _timeProvider.GetUtcNow().UtcDateTime.SafeAdd(expiresIn.Value) : DateTime.MaxValue;
        if (expiresAt < _timeProvider.GetUtcNow().UtcDateTime)
        {
            await ListRemoveAsync(key, values).AnyContext();
            return 0;
        }

        var redisValues = new List<SortedSetEntry>();
        long expiresAtMilliseconds = expiresAt.ToUnixTimeMilliseconds();

        if (values is string stringValue)
            redisValues.Add(new SortedSetEntry(stringValue.ToRedisValue(_options.Serializer), expiresAtMilliseconds));
        else
            redisValues.AddRange(values.Where(v => v is not null).Select(value => new SortedSetEntry(value.ToRedisValue(_options.Serializer), expiresAtMilliseconds)));

        await RemoveExpiredListValuesAsync<T>(key, values is string).AnyContext();

        if (redisValues.Count == 0)
            return 0;

        long added = await Database.SortedSetAddAsync(key, redisValues.ToArray()).AnyContext();
        if (added > 0)
            await SetListExpirationAsync(key).AnyContext();

        return added;
    }

    public async Task<long> ListRemoveAsync<T>(string key, IEnumerable<T> values, TimeSpan? expiresIn = null)
    {
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty");

        if (values == null)
            throw new ArgumentNullException(nameof(values));

        var redisValues = new List<RedisValue>();
        if (values is string stringValue)
            redisValues.Add(stringValue.ToRedisValue(_options.Serializer));
        else
            redisValues.AddRange(values.Where(v => v is not null).Select(value => value.ToRedisValue(_options.Serializer)));

        await RemoveExpiredListValuesAsync<T>(key, values is string).AnyContext();

        if (redisValues.Count == 0)
            return 0;

        long removed = await Database.SortedSetRemoveAsync(key, redisValues.ToArray()).AnyContext();
        if (removed > 0)
            await SetListExpirationAsync(key).AnyContext();

        return removed;
    }

    private const long MaxUnixEpochMilliseconds = 253_402_300_799_999L; // 9999-12-31T23:59:59.999Z

    private async Task SetListExpirationAsync(string key)
    {
        var items = await Database.SortedSetRangeByRankWithScoresAsync(key, 0, 0, order: Order.Descending).AnyContext();
        if (items.Length == 0)
            return;

        long highestExpirationInMs = (long)items.Single().Score;
        if (highestExpirationInMs > MaxUnixEpochMilliseconds)
        {
            await SetExpirationAsync(key, TimeSpan.MaxValue).AnyContext();
            return;
        }

        var furthestExpirationUtc = highestExpirationInMs.FromUnixTimeMilliseconds();
        var expiresIn = furthestExpirationUtc - _timeProvider.GetUtcNow();
        await SetExpirationAsync(key, expiresIn).AnyContext();
    }

    private async Task RemoveExpiredListValuesAsync<T>(string key, bool isStringValues)
    {
        try
        {
            long expiredValues = await Database
                .SortedSetRemoveRangeByScoreAsync(key, 0, _timeProvider.GetUtcNow().ToUnixTimeMilliseconds())
                .AnyContext();

            if (expiredValues > 0)
                _logger.LogTrace("Removed {ExpiredValues} expired values for key: {Key}", expiredValues, key);
        }
        catch (RedisServerException ex) when (ex.Message.StartsWith("WRONGTYPE"))
        {
            _logger.LogInformation(ex, "Migrating legacy set to sorted set for key: {Key}", key);

            // convert legacy set to sorted set
            var oldItems = await Database.SetMembersAsync(key).AnyContext();
            await Database.KeyDeleteAsync(key).AnyContext();

            var currentKeyExpiresIn = await GetExpirationAsync(key).AnyContext();
            if (isStringValues)
            {
                var oldItemValues = new List<string>(oldItems.Length);
                foreach (string oldItem in RedisValuesToCacheValue<string>(oldItems).Value)
                    oldItemValues.Add(oldItem);

                await ListAddAsync(key, oldItemValues, currentKeyExpiresIn).AnyContext();
            }
            else
            {
                var oldItemValues = new List<T>(oldItems.Length);
                foreach (var oldItem in RedisValuesToCacheValue<T>(oldItems).Value)
                    oldItemValues.Add(oldItem);

                await ListAddAsync(key, oldItemValues, currentKeyExpiresIn).AnyContext();
            }

            if (currentKeyExpiresIn.HasValue)
                await Database.KeyExpireAsync(key, (DateTime?)null).AnyContext();
        }
    }

    public Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiresIn = null)
    {
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty");

        return InternalSetAsync(key, value, expiresIn);
    }

    public async Task<double> SetIfHigherAsync(string key, double value, TimeSpan? expiresIn = null)
    {
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty");

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
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty");

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
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty");

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
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty");

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

    private async Task<bool> InternalSetAsync<T>(string key, T value, TimeSpan? expiresIn = null, When when = When.Always, CommandFlags flags = CommandFlags.None)
    {
        if (expiresIn?.Ticks <= 0)
        {
            _logger.LogTrace("Removing expired key: {Key}", key);
            await RemoveAsync(key).AnyContext();
            return false;
        }

        var redisValue = value.ToRedisValue(_options.Serializer);
        return await Database.StringSetAsync(key, redisValue, expiresIn, when, flags).AnyContext();
    }

    public async Task<int> SetAllAsync<T>(IDictionary<string, T> values, TimeSpan? expiresIn = null)
    {
        if (values == null || values.Count == 0)
            return 0;

        if (expiresIn?.Ticks <= 0)
        {
            _logger.LogTrace("Removing expired keys: {Keys}", values.Keys);
            await RemoveAllAsync(values.Keys).AnyContext();
            return 0;
        }

        var tasks = new List<Task<bool>>();
        foreach (var pair in values)
            tasks.Add(Database.StringSetAsync(pair.Key, pair.Value.ToRedisValue(_options.Serializer), expiresIn));

        bool[] results = await Task.WhenAll(tasks).AnyContext();
        return results.Count(r => r);
    }

    public Task<bool> ReplaceAsync<T>(string key, T value, TimeSpan? expiresIn = null)
    {
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty");

        return InternalSetAsync(key, value, expiresIn, When.Exists);
    }

    public async Task<bool> ReplaceIfEqualAsync<T>(string key, T value, T expected, TimeSpan? expiresIn = null)
    {
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty");

        await LoadScriptsAsync().AnyContext();

        var redisValue = value.ToRedisValue(_options.Serializer);
        var expectedValue = expected.ToRedisValue(_options.Serializer);
        RedisResult redisResult;

        // NOTE: If the expires is negative, we need to set it to 1ms to avoid an exception. We could look into replacing the operation to PEXPIRE
        if (expiresIn.HasValue)
            redisResult = await Database.ScriptEvaluateAsync(_replaceIfEqual, new { key = (RedisKey)key, value = redisValue, expected = expectedValue, expires = Math.Max((int)expiresIn.Value.TotalMilliseconds, 1) }).AnyContext();
        else
            redisResult = await Database.ScriptEvaluateAsync(_replaceIfEqual, new { key = (RedisKey)key, value = redisValue, expected = expectedValue, expires = "" }).AnyContext();

        var result = (int)redisResult;

        return result > 0;
    }

    public async Task<double> IncrementAsync(string key, double amount = 1, TimeSpan? expiresIn = null)
    {
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty");

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
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty");

        if (expiresIn?.Ticks <= 0)
        {
            _logger.LogTrace("Removing expired key: {Key}", key);
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
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty");

        return Database.KeyExistsAsync(key);
    }

    public Task<TimeSpan?> GetExpirationAsync(string key)
    {
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty");

        return Database.KeyTimeToLiveAsync(key);
    }

    public Task SetExpirationAsync(string key, TimeSpan expiresIn)
    {
        if (String.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key), "Key cannot be null or empty");

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
        _logger.LogInformation("Redis connection restored");
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
