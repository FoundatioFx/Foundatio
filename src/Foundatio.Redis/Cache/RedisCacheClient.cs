using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
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
    private bool? _supportsMsetEx;

    private LoadedLuaScript _incrementWithExpire;
    private LoadedLuaScript _removeIfEqual;
    private LoadedLuaScript _replaceIfEqual;
    private LoadedLuaScript _setIfHigher;
    private LoadedLuaScript _setIfLower;
    private LoadedLuaScript _getAllExpiration;
    private LoadedLuaScript _setAllExpiration;

    public RedisCacheClient(RedisCacheClientOptions options)
    {
        _options = options;
        _timeProvider = options.TimeProvider ?? TimeProvider.System;
        options.Serializer ??= DefaultSerializer.Instance;
        _logger = options.LoggerFactory?.CreateLogger(typeof(RedisCacheClient)) ?? NullLogger.Instance;

        options.ConnectionMultiplexer.ConnectionRestored += ConnectionMultiplexerOnConnectionRestored;
        options.ConnectionMultiplexer.ConnectionFailed += ConnectionMultiplexerOnConnectionFailed;
    }

    public RedisCacheClient(Builder<RedisCacheClientOptionsBuilder, RedisCacheClientOptions> config)
        : this(config(new RedisCacheClientOptionsBuilder()).Build())
    {
    }

    public IDatabase Database => _options.ConnectionMultiplexer.GetDatabase(_options.Database);

    public Task<bool> RemoveAsync(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        _logger.LogTrace("RemoveAsync: Removing key: {Key}", key);
        return Database.KeyDeleteAsync(key);
    }

    public async Task<bool> RemoveIfEqualAsync<T>(string key, T expected)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

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
        if (keys is null)
        {
            var endpoints = _options.ConnectionMultiplexer.GetEndPoints();
            if (endpoints.Length is 0)
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
                    Interlocked.Add(ref deleted, dbSize);
                    continue;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unable to flush database on {Endpoint}: {Message}", server.EndPoint, ex.Message);
                }

                try
                {
                    // NOTE: We need to use a HashSet to avoid duplicate counts due to SCAN is non-deterministic.
                    // A Performance win could be had if we are sure dbSize didn't fail and we know nothing was changing
                    // keys while we were deleting.
                    var seen = new HashSet<RedisKey>();
                    await foreach (var key in server.KeysAsync(_options.Database).ConfigureAwait(false))
                        seen.Add(key);

                    foreach (var batch in seen.Chunk(batchSize))
                        deleted += await Database.KeyDeleteAsync(batch.ToArray()).AnyContext();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error deleting all keys on {Endpoint}: {Message}", server.EndPoint, ex.Message);
                }
            }
        }
        else if (Database.Multiplexer.IsCluster())
        {
            var redisKeys = keys is ICollection<string> collection ? new List<RedisKey>(collection.Count) : [];
            foreach (string key in keys.Distinct())
            {
                ArgumentException.ThrowIfNullOrEmpty(key, nameof(keys));
                redisKeys.Add(key);
            }

            if (redisKeys.Count is 0)
                return 0;

            foreach (var batch in redisKeys.Chunk(batchSize))
            {
                // NOTE: Consider parallel processing per hash slot for performance optimization
                foreach (var hashSlotGroup in batch.GroupBy(k => Database.Multiplexer.HashSlot(k)))
                {
                    var hashSlotKeys = hashSlotGroup.ToArray();
                    try
                    {
                        long count = await Database.KeyDeleteAsync(hashSlotKeys).AnyContext();
                        Interlocked.Add(ref deleted, count);
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
            var redisKeys = keys is ICollection<string> collection ? new List<RedisKey>(collection.Count) : [];
            foreach (string key in keys.Distinct())
            {
                ArgumentException.ThrowIfNullOrEmpty(key, nameof(keys));
                redisKeys.Add(key);
            }

            if (redisKeys.Count is 0)
                return 0;

            // NOTE: Consider parallel processing of batches for performance optimization
            foreach (var batch in redisKeys.Chunk(batchSize))
            {
                try
                {
                    long count = await Database.KeyDeleteAsync(batch).AnyContext();
                    Interlocked.Add(ref deleted, count);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unable to delete keys ({Keys}): {Message}", batch, ex.Message);
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
                    // NOTE: Consider parallel processing per hash slot for performance optimization
                    foreach (var slotGroup in keys.GroupBy(k => _options.ConnectionMultiplexer.HashSlot(k)))
                    {
                        long count = await Database.KeyDeleteAsync(slotGroup.ToArray()).AnyContext();
                        Interlocked.Add(ref deleted, count);
                    }
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
        ArgumentException.ThrowIfNullOrEmpty(key);

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
        if (!redisValue.HasValue)
            return CacheValue<T>.NoValue;

        if (redisValue == _nullValue)
            return CacheValue<T>.Null;

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
        ArgumentNullException.ThrowIfNull(keys);

        var redisKeys = keys is ICollection<string> collection ? new List<RedisKey>(collection.Count) : [];
        foreach (string key in keys.Distinct())
        {
            ArgumentException.ThrowIfNullOrEmpty(key, nameof(keys));
            redisKeys.Add(key);
        }

        if (redisKeys.Count is 0)
            return ReadOnlyDictionary<string, CacheValue<T>>.Empty;

        if (_options.ConnectionMultiplexer.IsCluster())
        {
            var result = new Dictionary<string, CacheValue<T>>(redisKeys.Count);
            // NOTE: Consider parallel processing per hash slot for performance optimization
            foreach (var hashSlotGroup in redisKeys.GroupBy(k => _options.ConnectionMultiplexer.HashSlot(k)))
            {
                var hashSlotKeys = hashSlotGroup.ToArray();
                var values = await Database.StringGetAsync(hashSlotKeys, _options.ReadMode).AnyContext();

                // Redis MGET guarantees that values are returned in the same order as keys.
                // Non-existent keys return nil/empty values in their respective positions.
                // https://redis.io/commands/mget
                for (int i = 0; i < hashSlotKeys.Length; i++)
                    result[hashSlotKeys[i]] = RedisValueToCacheValue<T>(values[i]);
            }

            return result.AsReadOnly();
        }
        else
        {
            var result = new Dictionary<string, CacheValue<T>>(redisKeys.Count);
            var values = await Database.StringGetAsync(redisKeys.ToArray(), _options.ReadMode).AnyContext();

            // Redis MGET guarantees that values are returned in the same order as keys
            for (int i = 0; i < redisKeys.Count; i++)
                result[redisKeys[i]] = RedisValueToCacheValue<T>(values[i]);

            return result.AsReadOnly();
        }
    }

    public async Task<CacheValue<ICollection<T>>> GetListAsync<T>(string key, int? page = null, int pageSize = 100)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (page is < 1)
            throw new ArgumentOutOfRangeException(nameof(page), "Page cannot be less than 1");

        try
        {
            if (!page.HasValue)
            {
                var set = await Database.SortedSetRangeByScoreAsync(key, _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(), Double.PositiveInfinity, Exclude.Start, flags: _options.ReadMode).AnyContext();
                return RedisValuesToCacheValue<T>(set);
            }
            else
            {
                long skip = (page.Value - 1) * pageSize;
                var set = await Database.SortedSetRangeByScoreAsync(key, _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(), Double.PositiveInfinity, Exclude.Start, skip: skip, take: pageSize, flags: _options.ReadMode).AnyContext();
                return RedisValuesToCacheValue<T>(set);
            }
        }
        catch (RedisServerException ex) when (ex.Message.StartsWith("WRONGTYPE"))
        {
            _logger.LogInformation(ex, "Migrating legacy set to sorted set for key: {Key}", key);
            await MigrateLegacySetToSortedSetForKeyAsync<T>(key, typeof(T) == typeof(string)).AnyContext();
            return await GetListAsync<T>(key, page, pageSize).AnyContext();
        }
    }

    public Task<bool> AddAsync<T>(string key, T value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        return InternalSetAsync(key, value, expiresIn, When.NotExists);
    }

    public async Task<long> ListAddAsync<T>(string key, IEnumerable<T> values, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(values);

        if (expiresIn is { Ticks: <= 0 })
        {
            await ListRemoveAsync(key, values).AnyContext();
            return 0;
        }

        var expiresAt = expiresIn.HasValue ? _timeProvider.GetUtcNow().UtcDateTime.SafeAdd(expiresIn.Value) : DateTime.MaxValue;

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
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(values);

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
        {
            _logger.LogTrace("Sorted set is empty for key: {Key}, expiration will not be set", key);
            return;
        }

        long highestExpirationInMs = (long)items.Single().Score;
        if (highestExpirationInMs >= MaxUnixEpochMilliseconds)
        {
            // Items have max expiration, remove any existing TTL (persist the key)
            await Database.KeyPersistAsync(key).AnyContext();
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
            await MigrateLegacySetToSortedSetForKeyAsync<T>(key, isStringValues).AnyContext();
        }
    }

    private async Task MigrateLegacySetToSortedSetForKeyAsync<T>(string key, bool isStringValues)
    {
        // convert legacy set to sorted set
        var oldItems = await Database.SetMembersAsync(key).AnyContext();
        var currentKeyExpiresIn = await GetExpirationAsync(key).AnyContext();
        await Database.KeyDeleteAsync(key).AnyContext();
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
    }

    public Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        return InternalSetAsync(key, value, expiresIn);
    }

    public async Task<double> SetIfHigherAsync(string key, double value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (expiresIn is { Ticks: <= 0 })
        {
            await RemoveAsync(key).AnyContext();
            return 0;
        }

        await LoadScriptsAsync().AnyContext();

        var expiresMs = GetExpirationMilliseconds(expiresIn);
        var expiresArg = expiresMs.HasValue ? (RedisValue)expiresMs.Value : RedisValue.EmptyString;
        var result = await Database.ScriptEvaluateAsync(_setIfHigher, new { key = (RedisKey)key, value, expires = expiresArg }).AnyContext();
        return (double)result;
    }

    public async Task<long> SetIfHigherAsync(string key, long value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (expiresIn is { Ticks: <= 0 })
        {
            await RemoveAsync(key).AnyContext();
            return 0;
        }

        await LoadScriptsAsync().AnyContext();

        var expiresMs = GetExpirationMilliseconds(expiresIn);
        var expiresArg = expiresMs.HasValue ? (RedisValue)expiresMs.Value : RedisValue.EmptyString;
        var result = await Database.ScriptEvaluateAsync(_setIfHigher, new { key = (RedisKey)key, value, expires = expiresArg }).AnyContext();
        return (long)result;
    }

    public async Task<double> SetIfLowerAsync(string key, double value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (expiresIn is { Ticks: <= 0 })
        {
            await RemoveAsync(key).AnyContext();
            return 0;
        }

        await LoadScriptsAsync().AnyContext();

        var expiresMs = GetExpirationMilliseconds(expiresIn);
        var expiresArg = expiresMs.HasValue ? (RedisValue)expiresMs.Value : RedisValue.EmptyString;
        var result = await Database.ScriptEvaluateAsync(_setIfLower, new { key = (RedisKey)key, value, expires = expiresArg }).AnyContext();
        return (double)result;
    }

    public async Task<long> SetIfLowerAsync(string key, long value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (expiresIn is { Ticks: <= 0 })
        {
            await RemoveAsync(key).AnyContext();
            return 0;
        }

        await LoadScriptsAsync().AnyContext();

        var expiresMs = GetExpirationMilliseconds(expiresIn);
        var expiresArg = expiresMs.HasValue ? (RedisValue)expiresMs.Value : RedisValue.EmptyString;
        var result = await Database.ScriptEvaluateAsync(_setIfLower, new { key = (RedisKey)key, value, expires = expiresArg }).AnyContext();
        return (long)result;
    }

    private async Task<bool> InternalSetAsync<T>(string key, T value, TimeSpan? expiresIn = null, When when = When.Always, CommandFlags flags = CommandFlags.None)
    {
        if (expiresIn?.Ticks <= 0)
        {
            _logger.LogTrace("Removing expired key: {Key}", key);
            await RemoveAsync(key).AnyContext();
            return false;
        }

        expiresIn = NormalizeExpiration(expiresIn);

        var redisValue = value.ToRedisValue(_options.Serializer);
        return await Database.StringSetAsync(key, redisValue, expiresIn, when, flags).AnyContext();
    }

    public async Task<int> SetAllAsync<T>(IDictionary<string, T> values, TimeSpan? expiresIn = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count is 0)
            return 0;

        if (expiresIn?.Ticks <= 0)
        {
            _logger.LogTrace("Removing expired keys: {Keys}", values.Keys);
            await RemoveAllAsync(values.Keys).AnyContext();
            return 0;
        }

        expiresIn = NormalizeExpiration(expiresIn);

        // Validate keys and serialize values upfront
        var pairs = new KeyValuePair<RedisKey, RedisValue>[values.Count];
        int index = 0;
        foreach (var kvp in values)
        {
            ArgumentException.ThrowIfNullOrEmpty(kvp.Key, nameof(values));
            pairs[index++] = new KeyValuePair<RedisKey, RedisValue>(kvp.Key, kvp.Value.ToRedisValue(_options.Serializer));
        }

        if (_options.ConnectionMultiplexer.IsCluster())
        {
            // For cluster/sentinel, group keys by hash slot since batch operations
            // require all keys to be in the same slot
            // NOTE: Consider parallel processing per hash slot for performance optimization
            int successCount = 0;
            foreach (var slotGroup in pairs.GroupBy(p => _options.ConnectionMultiplexer.HashSlot(p.Key)))
            {
                int count = await SetAllInternalAsync(slotGroup.ToArray(), expiresIn).AnyContext();
                Interlocked.Add(ref successCount, count);
            }
            return successCount;
        }

        return await SetAllInternalAsync(pairs, expiresIn).AnyContext();
    }

    /// <summary>
    /// Internal method that performs the actual batch set operation.
    /// All keys in <paramref name="pairs"/> must be in the same hash slot for cluster mode.
    /// </summary>
    private async Task<int> SetAllInternalAsync(KeyValuePair<RedisKey, RedisValue>[] pairs, TimeSpan? expiresIn)
    {
        if (expiresIn.HasValue)
        {
            // With expiration: use MSETEX if available, otherwise fall back to pipelined SETs
            if (SupportsMsetexCommand())
            {
                bool success = await Database.StringSetAsync(pairs, When.Always, new Expiration(expiresIn.Value)).AnyContext();
                return success ? pairs.Length : 0;
            }

            // Fallback for Redis < 8.4: pipelined individual SET commands with expiration
            var tasks = new List<Task<bool>>(pairs.Length);
            foreach (var pair in pairs)
            {
                tasks.Add(Database.StringSetAsync(pair.Key, pair.Value, expiresIn, When.Always));
            }

            bool[] results = await Task.WhenAll(tasks).AnyContext();
            return results.Count(r => r);
        }

        // No expiration: use MSET (available since Redis 1.0.1) - single atomic batch operation
        bool msetSuccess = await Database.StringSetAsync(pairs).AnyContext();
        return msetSuccess ? pairs.Length : 0;
    }

    /// <summary>
    /// Checks if the connected Redis server supports the MSETEX command (Redis 8.4+).
    /// </summary>
    /// <remarks>
    /// <para>
    /// MSETEX allows setting multiple keys with expiration in a single atomic operation.
    /// StackExchange.Redis 2.10.1+ supports this via the <c>StringSetAsync</c> overload that
    /// accepts an <see cref="Expiration"/> parameter. However, SE.Redis does NOT automatically
    /// fall back to individual SET commands on older Redis versions - it will fail.
    /// </para>
    /// <para>
    /// This method detects the Redis server version and caches the result. The cache is
    /// invalidated when the connection is restored (e.g., after failover to a different server).
    /// </para>
    /// <para>
    /// Note: For batch sets WITHOUT expiration, use <c>StringSetAsync(pairs, When, CommandFlags)</c>
    /// which uses MSET - available since Redis 1.0.1 and doesn't require version detection.
    /// MSET replaces existing values and removes any existing TTL, just like regular SET.
    /// </para>
    /// </remarks>
    /// <returns>
    /// <c>true</c> if all connected primary servers support MSETEX;
    /// <c>false</c> if any primary is running Redis &lt; 8.4 or no primaries are connected.
    /// </returns>
    private bool SupportsMsetexCommand()
    {
        if (_supportsMsetEx.HasValue)
            return _supportsMsetEx.Value;

        // Redis 8.4 RC1 is internally versioned as 8.3.224
        var minVersion = new Version(8, 3, 224);

        var endpoints = _options.ConnectionMultiplexer.GetEndPoints();
        if (endpoints.Length == 0)
        {
            _logger.LogDebug("SupportsMsetexCommand: No endpoints configured, MSETEX not available");
            return false; // Don't cache - no endpoints configured
        }

        bool foundConnectedPrimary = false;
        foreach (var endpoint in endpoints)
        {
            var server = _options.ConnectionMultiplexer.GetServer(endpoint);
            if (server.IsConnected && !server.IsReplica)
            {
                foundConnectedPrimary = true;
                if (server.Version < minVersion)
                {
                    _logger.LogDebug("SupportsMsetexCommand: Server {Endpoint} version {Version} does not support MSETEX (requires {MinVersion}+)",
                        endpoint, server.Version, minVersion);
                    _supportsMsetEx = false;
                    return false;
                }
            }
        }

        if (foundConnectedPrimary)
        {
            _supportsMsetEx = true;
            return true;
        }

        _logger.LogDebug("SupportsMsetexCommand: No connected primaries found, MSETEX availability unknown");
        // No connected primaries found - don't cache, will retry on next call
        return false;
    }

    public Task<bool> ReplaceAsync<T>(string key, T value, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        return InternalSetAsync(key, value, expiresIn, When.Exists);
    }

    public async Task<bool> ReplaceIfEqualAsync<T>(string key, T value, T expected, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (expiresIn is { Ticks: <= 0 })
        {
            _logger.LogTrace("Removing expired key: {Key}", key);
            await RemoveAsync(key).AnyContext();
            return false;
        }

        await LoadScriptsAsync().AnyContext();

        var redisValue = value.ToRedisValue(_options.Serializer);
        var expectedValue = expected.ToRedisValue(_options.Serializer);

        var expiresMs = GetExpirationMilliseconds(expiresIn);
        var expiresArg = expiresMs.HasValue ? (RedisValue)expiresMs.Value : RedisValue.EmptyString;
        var redisResult = await Database.ScriptEvaluateAsync(_replaceIfEqual, new { key = (RedisKey)key, value = redisValue, expected = expectedValue, expires = expiresArg }).AnyContext();

        var result = (int)redisResult;

        return result > 0;
    }

    public async Task<double> IncrementAsync(string key, double amount = 1, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (expiresIn is { Ticks: <= 0 })
        {
            _logger.LogTrace("Removing expired key: {Key}", key);
            await RemoveAsync(key).AnyContext();
            return 0;
        }

        var expiresMs = GetExpirationMilliseconds(expiresIn);

        // If no expiration needed, use native Redis command (preserves existing TTL)
        if (!expiresMs.HasValue)
            return await Database.StringIncrementAsync(key, amount).AnyContext();

        // Need to set expiration - use Lua script for atomicity
        await LoadScriptsAsync().AnyContext();
        var result = await Database.ScriptEvaluateAsync(_incrementWithExpire, new { key = (RedisKey)key, value = amount, expires = expiresMs.Value }).AnyContext();
        return (double)result;
    }

    public async Task<long> IncrementAsync(string key, long amount = 1, TimeSpan? expiresIn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (expiresIn is { Ticks: <= 0 })
        {
            _logger.LogTrace("Removing expired key: {Key}", key);
            await RemoveAsync(key).AnyContext();
            return 0;
        }

        var expiresMs = GetExpirationMilliseconds(expiresIn);

        // If no expiration needed, use native Redis command (preserves existing TTL)
        if (!expiresMs.HasValue)
            return await Database.StringIncrementAsync(key, amount).AnyContext();

        // Need to set expiration - use Lua script for atomicity
        await LoadScriptsAsync().AnyContext();
        var result = await Database.ScriptEvaluateAsync(_incrementWithExpire, new { key = (RedisKey)key, value = amount, expires = expiresMs.Value }).AnyContext();
        return (long)result;
    }

    public Task<bool> ExistsAsync(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        return Database.KeyExistsAsync(key);
    }

    public Task<TimeSpan?> GetExpirationAsync(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        return Database.KeyTimeToLiveAsync(key);
    }

    public Task SetExpirationAsync(string key, TimeSpan expiresIn)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        // TimeSpan.MaxValue means "no expiration" - persist the key to remove any existing TTL
        if (expiresIn == TimeSpan.MaxValue)
            return Database.KeyPersistAsync(key);

        return Database.KeyExpireAsync(key, expiresIn);
    }

    public async Task<IDictionary<string, TimeSpan?>> GetAllExpirationAsync(IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        var keyList = keys is ICollection<string> collection ? new List<RedisKey>(collection.Count) : [];
        foreach (string key in keys.Distinct())
        {
            ArgumentException.ThrowIfNullOrEmpty(key, nameof(keys));
            keyList.Add(key);
        }

        if (keyList.Count is 0)
            return ReadOnlyDictionary<string, TimeSpan?>.Empty;

        await LoadScriptsAsync().AnyContext();

        if (_options.ConnectionMultiplexer.IsCluster())
        {
            var result = new Dictionary<string, TimeSpan?>(keyList.Count);
            // NOTE: Consider parallel processing per hash slot for performance optimization
            foreach (var hashSlotGroup in keyList.GroupBy(k => _options.ConnectionMultiplexer.HashSlot(k)))
            {
                var hashSlotKeys = hashSlotGroup.ToArray();
                var redisResult = await Database.ScriptEvaluateAsync(_getAllExpiration.Hash, hashSlotKeys).AnyContext();
                if (redisResult.IsNull)
                    continue;

                // Lua script uses Redis PTTL command which returns TTL in milliseconds.
                // PTTL return values (https://redis.io/docs/latest/commands/pttl/):
                //   -2 = Key does not exist
                //   -1 = Key exists but has no associated expiration
                //   Positive integer = Remaining TTL in milliseconds
                long[] ttls = (long[])redisResult;
                if (ttls is null || ttls.Length != hashSlotKeys.Length)
                    throw new InvalidOperationException($"Script returned {ttls?.Length ?? 0} results for {hashSlotKeys.Length} keys");

                for (int hashSlotIndex = 0; hashSlotIndex < hashSlotKeys.Length; hashSlotIndex++)
                {
                    string key = hashSlotKeys[hashSlotIndex];
                    long ttl = ttls[hashSlotIndex];
                    if (ttl == -2) // Key doesn't exist - omit from result
                        continue;
                    if (ttl == -1) // Key exists but no expiration - include with null value
                        result[key] = null;
                    else // Key has TTL - include with TimeSpan value
                        result[key] = TimeSpan.FromMilliseconds(ttl);
                }
            }

            return result.AsReadOnly();
        }
        else
        {
            var redisKeys = keyList.ToArray();
            var redisResult = await Database.ScriptEvaluateAsync(_getAllExpiration.Hash, redisKeys).AnyContext();

            if (redisResult.IsNull)
                return ReadOnlyDictionary<string, TimeSpan?>.Empty;

            // Lua script uses Redis PTTL command which returns TTL in milliseconds.
            // PTTL return values (https://redis.io/docs/latest/commands/pttl/):
            //   -2 = Key does not exist
            //   -1 = Key exists but has no associated expiration
            //   Positive integer = Remaining TTL in milliseconds
            long[] ttls = (long[])redisResult;
            if (ttls is null || ttls.Length != redisKeys.Length)
                throw new InvalidOperationException($"Script returned {ttls?.Length ?? 0} results for {redisKeys.Length} keys");

            var result = new Dictionary<string, TimeSpan?>();
            for (int keyIndex = 0; keyIndex < redisKeys.Length; keyIndex++)
            {
                string key = redisKeys[keyIndex];
                long ttl = ttls[keyIndex];
                if (ttl == -2) // Key doesn't exist - omit from result
                    continue;
                if (ttl == -1) // Key exists but no expiration - include with null value
                    result[key] = null;
                else // Key has TTL - include with TimeSpan value
                    result[key] = TimeSpan.FromMilliseconds(ttl);
            }

            return result.AsReadOnly();
        }
    }

    public async Task SetAllExpirationAsync(IDictionary<string, TimeSpan?> expirations)
    {
        ArgumentNullException.ThrowIfNull(expirations);
        if (expirations.Count == 0)
            return;

        if (expirations.ContainsKey(String.Empty))
            throw new ArgumentException("Keys cannot be empty", nameof(expirations));

        await LoadScriptsAsync().AnyContext();

        if (_options.ConnectionMultiplexer.IsCluster())
        {
            // NOTE: Consider parallel processing per hash slot for performance optimization
            foreach (var hashSlotGroup in expirations.GroupBy(kvp => _options.ConnectionMultiplexer.HashSlot(kvp.Key)))
            {
                var hashSlotExpirations = hashSlotGroup.ToList();
                var keys = hashSlotExpirations.Select(kvp => (RedisKey)kvp.Key).ToArray();
                var values = hashSlotExpirations
                    .Select(kvp => (RedisValue)(kvp.Value.HasValue ? (long)kvp.Value.Value.TotalMilliseconds : -1))
                    .ToArray();

                await Database.ScriptEvaluateAsync(_setAllExpiration.Hash, keys, values).AnyContext();
            }
        }
        else
        {
            var keys = expirations.Select(kvp => (RedisKey)kvp.Key).ToArray();
            var values = expirations
                .Select(kvp => (RedisValue)(kvp.Value.HasValue ? (long)kvp.Value.Value.TotalMilliseconds : -1))
                .ToArray();

            await Database.ScriptEvaluateAsync(_setAllExpiration.Hash, keys, values).AnyContext();
        }
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
            var getAllExpiration = LuaScript.Prepare(GetAllExpirationScript);
            var setAllExpiration = LuaScript.Prepare(SetAllExpirationScript);

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
                _getAllExpiration = await getAllExpiration.LoadAsync(server).AnyContext();
                _setAllExpiration = await setAllExpiration.LoadAsync(server).AnyContext();
            }

            _scriptsLoaded = true;
        }
    }

    private void ConnectionMultiplexerOnConnectionRestored(object sender, ConnectionFailedEventArgs connectionFailedEventArgs)
    {
        _logger.LogInformation("Redis connection restored");
        _scriptsLoaded = false;
        _supportsMsetEx = null; // Re-check version on next call
    }

    private void ConnectionMultiplexerOnConnectionFailed(object sender, ConnectionFailedEventArgs connectionFailedEventArgs)
    {
        _logger.LogWarning("Redis connection failed: {FailureType}", connectionFailedEventArgs.FailureType);
    }

    public void Dispose()
    {
        _options.ConnectionMultiplexer.ConnectionRestored -= ConnectionMultiplexerOnConnectionRestored;
        _options.ConnectionMultiplexer.ConnectionFailed -= ConnectionMultiplexerOnConnectionFailed;
    }

    /// <summary>
    /// Normalizes expiration: TimeSpan.MaxValue or overflow to DateTime.MaxValue means "no expiration".
    /// </summary>
    private TimeSpan? NormalizeExpiration(TimeSpan? expiresIn)
    {
        if (!expiresIn.HasValue || expiresIn.Value == TimeSpan.MaxValue)
            return null;

        var expiresAt = _timeProvider.GetUtcNow().UtcDateTime.SafeAdd(expiresIn.Value);
        return expiresAt == DateTime.MaxValue ? null : expiresIn;
    }

    /// <summary>
    /// Gets the expiration in milliseconds for use in Lua scripts.
    /// Returns null if no expiration should be set (null, TimeSpan.MaxValue, or overflow to DateTime.MaxValue).
    /// </summary>
    private long? GetExpirationMilliseconds(TimeSpan? expiresIn)
    {
        if (!expiresIn.HasValue || expiresIn.Value == TimeSpan.MaxValue)
            return null;

        // Check if adding expiresIn to now would overflow to DateTime.MaxValue
        var expiresAt = _timeProvider.GetUtcNow().UtcDateTime.SafeAdd(expiresIn.Value);
        if (expiresAt == DateTime.MaxValue)
            return null;

        return (long)expiresIn.Value.TotalMilliseconds;
    }

    ISerializer IHaveSerializer.Serializer => _options.Serializer;

    private static readonly string IncrementWithScript = EmbeddedResourceLoader.GetEmbeddedResource("Foundatio.Redis.Scripts.IncrementWithExpire.lua");
    private static readonly string RemoveIfEqualScript = EmbeddedResourceLoader.GetEmbeddedResource("Foundatio.Redis.Scripts.RemoveIfEqual.lua");
    private static readonly string ReplaceIfEqualScript = EmbeddedResourceLoader.GetEmbeddedResource("Foundatio.Redis.Scripts.ReplaceIfEqual.lua");
    private static readonly string SetIfHigherScript = EmbeddedResourceLoader.GetEmbeddedResource("Foundatio.Redis.Scripts.SetIfHigher.lua");
    private static readonly string SetIfLowerScript = EmbeddedResourceLoader.GetEmbeddedResource("Foundatio.Redis.Scripts.SetIfLower.lua");
    private static readonly string GetAllExpirationScript = EmbeddedResourceLoader.GetEmbeddedResource("Foundatio.Redis.Scripts.GetAllExpiration.lua");
    private static readonly string SetAllExpirationScript = EmbeddedResourceLoader.GetEmbeddedResource("Foundatio.Redis.Scripts.SetAllExpiration.lua");
}

