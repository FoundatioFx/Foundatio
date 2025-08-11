using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Foundatio.AsyncEx;
using Foundatio.Caching;
using Foundatio.Messaging;
using Foundatio.Tests.Extensions;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Caching;

public class HybridCacheClientTestBase : CacheClientTestsBase, IDisposable
{
    protected readonly ICacheClient _distributedCache;
    protected readonly IMessageBus _messageBus;

    public HybridCacheClientTestBase(ITestOutputHelper output) : base(output)
    {
        _distributedCache = new InMemoryCacheClient(o => o.CloneValues(true).ShouldThrowOnSerializationError(true).LoggerFactory(Log));
        _messageBus = new InMemoryMessageBus(o => o.LoggerFactory(Log));
    }

    /// <summary>
    /// Returns a hybrid cache client that has a distributed shared cache. For in memory tests, this will be overridden with a shared cache.
    /// </summary>
    protected virtual HybridCacheClient GetDistributedHybridCacheClient(bool shouldThrowOnSerializationError = true)
    {
        return GetCacheClient(shouldThrowOnSerializationError) switch
        {
            HybridCacheClient hybridCacheClient => hybridCacheClient,
            ScopedCacheClient { UnscopedCache: HybridCacheClient hybridCacheClient } => hybridCacheClient,
            _ => throw new InvalidOperationException("The provided cache client is not a hybrid cache client.")
        };
    }

    protected virtual async Task WillUseLocalCache()
    {
        using var firstCache = GetDistributedHybridCacheClient();
        Assert.NotNull(firstCache);

        using var secondCache = GetDistributedHybridCacheClient();
        Assert.NotNull(secondCache);

        await firstCache.SetAsync("first1", 1);
        await firstCache.IncrementAsync("first2");
        Assert.Equal(1, firstCache.LocalCache.Count);

        string cacheKey = Guid.NewGuid().ToString("N").Substring(10);
        await firstCache.SetAsync(cacheKey, new SimpleModel { Data1 = "test" });
        Assert.Equal(2, firstCache.LocalCache.Count);
        Assert.Equal(0, secondCache.LocalCache.Count);
        Assert.Equal(0, firstCache.LocalCacheHits);

        Assert.True((await firstCache.GetAsync<SimpleModel>(cacheKey)).HasValue);
        Assert.Equal(1, firstCache.LocalCacheHits);
        Assert.Equal(2, firstCache.LocalCache.Count);

        Assert.True((await secondCache.GetAsync<SimpleModel>(cacheKey)).HasValue);
        Assert.Equal(0, secondCache.LocalCacheHits);
        Assert.Equal(1, secondCache.LocalCache.Count);

        Assert.True((await secondCache.GetAsync<SimpleModel>(cacheKey)).HasValue);
        Assert.Equal(1, secondCache.LocalCacheHits);
    }

    protected virtual async Task WillExpireRemoteItems()
    {
        using var firstCache = GetDistributedHybridCacheClient();
        Assert.NotNull(firstCache);
        var firstResetEvent = new AsyncAutoResetEvent(false);

        void ExpiredHandler(object sender, ItemExpiredEventArgs args)
        {
            _logger.LogTrace("First local cache expired: {Key}", args.Key);
            firstResetEvent.Set();
        }

        using (firstCache.LocalCache.ItemExpired.AddSyncHandler(ExpiredHandler))
        {
            using var secondCache = GetDistributedHybridCacheClient();
            Assert.NotNull(secondCache);
            var secondResetEvent = new AsyncAutoResetEvent(false);

            void ExpiredHandler2(object sender, ItemExpiredEventArgs args)
            {
                _logger.LogTrace("Second local cache expired: {Key}", args.Key);
                secondResetEvent.Set();
            }

            using (secondCache.LocalCache.ItemExpired.AddSyncHandler(ExpiredHandler2))
            {
                string cacheKey = "will-expire-remote";
                _logger.LogTrace("First Set");
                Assert.True(await firstCache.AddAsync(cacheKey, new SimpleModel { Data1 = "test" }, TimeSpan.FromMilliseconds(250)));
                _logger.LogTrace("Done First Set");
                Assert.Equal(1, firstCache.LocalCache.Count);

                _logger.LogTrace("Second Get");
                Assert.True((await secondCache.GetAsync<SimpleModel>(cacheKey)).HasValue);
                _logger.LogTrace("Done Second Get");
                Assert.Equal(1, secondCache.LocalCache.Count);

                _logger.LogTrace("Waiting for item expired handlers...");
                var sw = Stopwatch.StartNew();
                await firstResetEvent.WaitAsync(TimeSpan.FromSeconds(2));
                await secondResetEvent.WaitAsync(TimeSpan.FromSeconds(2));
                sw.Stop();
                _logger.LogTrace("Time {Elapsed:g}", sw.Elapsed);
            }
        }
    }

    protected virtual async Task WillWorkWithSets()
    {
        using var firstCache = GetDistributedHybridCacheClient();
        Assert.NotNull(firstCache);

        using var secondCache = GetDistributedHybridCacheClient();
        Assert.NotNull(secondCache);

        await firstCache.ListAddAsync("set1", [1, 2, 3]);
        var values = await secondCache.GetListAsync<int>("set1");
        Assert.Equal(3, values.Value.Count);
    }

    [Fact]
    public virtual async Task CanInvalidateLocalCacheViaRemoveAllAsync()
    {
        using var firstCache = GetDistributedHybridCacheClient();
        Assert.NotNull(firstCache);

        using var secondCache = GetDistributedHybridCacheClient();
        Assert.NotNull(secondCache);

        const string cacheKey = "key";

        // Set a value in the first cache first
        Assert.True(await firstCache.AddAsync(cacheKey, "value"));
        Assert.Equal(1, firstCache.LocalCache.Count);
        Assert.Equal(0, firstCache.InvalidateCacheCalls);

        Assert.Equal(0, secondCache.LocalCache.Count);
        Assert.Equal("value", (await secondCache.GetAsync<string>(cacheKey)).Value);
        Assert.Equal(1, secondCache.LocalCache.Count);

        Assert.Equal(1, await firstCache.RemoveAllAsync());
        Assert.Equal(0, firstCache.InvalidateCacheCalls);
        Assert.Equal(0, firstCache.LocalCache.Count);

        await Task.Delay(250); // Allow time for local cache to clear
        Assert.Equal(1, secondCache.InvalidateCacheCalls);
        Assert.Equal(0, secondCache.LocalCache.Count);
    }

    protected virtual async Task CanInvalidateLocalCacheViaRemoveByPrefixAsync()
    {
        using var firstCache = GetDistributedHybridCacheClient();
        Assert.NotNull(firstCache);

        using var secondCache = GetDistributedHybridCacheClient();
        Assert.NotNull(secondCache);

        const string cacheKey = "test-key";

        // Set a value in the first cache first
        Assert.True(await firstCache.AddAsync(cacheKey, "value"));
        Assert.Equal(1, firstCache.LocalCache.Count);
        Assert.Equal(0, firstCache.InvalidateCacheCalls);

        Assert.Equal(0, secondCache.LocalCache.Count);
        Assert.Equal("value", (await secondCache.GetAsync<string>(cacheKey)).Value);
        Assert.Equal(1, secondCache.LocalCache.Count);

        Assert.Equal(1, await firstCache.RemoveByPrefixAsync("test-"));
        Assert.Equal(0, firstCache.InvalidateCacheCalls);
        Assert.Equal(0, firstCache.LocalCache.Count);

        await Task.Delay(250); // Allow time for local cache to clear
        Assert.Equal(1, secondCache.InvalidateCacheCalls);
        Assert.Equal(0, secondCache.LocalCache.Count);
    }

        protected virtual async Task ExistsAsyncShouldCheckLocalCacheFirst()
    {
        using var cache = GetDistributedHybridCacheClient();
        Assert.NotNull(cache);

        const string key = "exists-local-test";

        // Set value so it exists in both local and distributed cache
        await cache.SetAsync(key, "test-value");
        Assert.Equal(1, cache.LocalCache.Count);
        Assert.Equal(0, cache.LocalCacheHits);

        // First call should hit local cache and increment counter
        Assert.True(await cache.ExistsAsync(key));
        Assert.Equal(1, cache.LocalCacheHits);

        // Second call should also hit local cache
        Assert.True(await cache.ExistsAsync(key));
        Assert.Equal(2, cache.LocalCacheHits);

        // Remove from local cache only (simulate local expiration)
        await cache.LocalCache.RemoveAsync(key);
        Assert.Equal(0, cache.LocalCache.Count);

        // Should now check distributed cache and return true
        Assert.True(await cache.ExistsAsync(key));
        Assert.Equal(2, cache.LocalCacheHits); // No increment since it was a miss
    }

    protected virtual async Task GetExpirationAsyncShouldCheckLocalCacheFirst()
    {
        using var cache = GetDistributedHybridCacheClient();
        Assert.NotNull(cache);

        const string key = "expiration-local-test";
        var expiration = TimeSpan.FromMinutes(5);

        // Set value with expiration
        await cache.SetAsync(key, "test-value", expiration);
        Assert.Equal(1, cache.LocalCache.Count);
        Assert.Equal(0, cache.LocalCacheHits);

        // First call should hit local cache and increment counter
        var result = await cache.GetExpirationAsync(key);
        Assert.NotNull(result);
        Assert.True(result.Value > TimeSpan.Zero);
        Assert.Equal(1, cache.LocalCacheHits);

        // Second call should also hit local cache
        result = await cache.GetExpirationAsync(key);
        Assert.NotNull(result);
        Assert.Equal(2, cache.LocalCacheHits);

        // Remove from local cache only
        await cache.LocalCache.RemoveAsync(key);
        Assert.Equal(0, cache.LocalCache.Count);

        // Should now check distributed cache
        result = await cache.GetExpirationAsync(key);
        Assert.NotNull(result);
        Assert.Equal(2, cache.LocalCacheHits); // No increment since it was a miss
    }

    protected virtual async Task GetAllAsyncShouldUseHybridCache()
    {
        using var cache = GetDistributedHybridCacheClient();
        Assert.NotNull(cache);

        // Set some values
        await cache.SetAsync("hybrid1", 1);
        await cache.SetAsync("hybrid2", 2);
        await cache.SetAsync("hybrid3", 3);
        Assert.Equal(3, cache.LocalCache.Count);
        Assert.Equal(0, cache.LocalCacheHits);

        // GetAll should hit local cache for all keys
        var result = await cache.GetAllAsync<int>(["hybrid1", "hybrid2", "hybrid3"]);
        Assert.Equal(3, result.Count);
        Assert.Equal(1, result["hybrid1"].Value);
        Assert.Equal(2, result["hybrid2"].Value);
        Assert.Equal(3, result["hybrid3"].Value);
        Assert.Equal(3, cache.LocalCacheHits);

        // Remove one key from local cache only
        await cache.LocalCache.RemoveAsync("hybrid2");
        Assert.Equal(2, cache.LocalCache.Count);

        // GetAll should hit local cache for 2 keys, distributed for 1
        result = await cache.GetAllAsync<int>(["hybrid1", "hybrid2", "hybrid3"]);
        Assert.Equal(3, result.Count);
        Assert.Equal(1, result["hybrid1"].Value);
        Assert.Equal(2, result["hybrid2"].Value); // From distributed cache
        Assert.Equal(3, result["hybrid3"].Value);
        Assert.Equal(5, cache.LocalCacheHits); // +2 for hybrid1 and hybrid3
        Assert.Equal(3, cache.LocalCache.Count); // hybrid2 should be back in local cache
    }

    protected virtual async Task GetAllAsyncShouldHandleEmptyKeys()
    {
        using var cache = GetDistributedHybridCacheClient();
        Assert.NotNull(cache);

        // Empty array should return empty dictionary without error
        var result = await cache.GetAllAsync<int>(Array.Empty<string>());
        Assert.NotNull(result);
        Assert.Empty(result);
        Assert.Equal(0, cache.LocalCacheHits);
    }

    protected virtual async Task GetAllAsyncShouldSkipNullKeys()
    {
        using var cache = GetDistributedHybridCacheClient();
        Assert.NotNull(cache);

        await cache.SetAsync("valid1", 1);
        await cache.SetAsync("valid2", 2);

        // Mix of valid and null/empty keys
        var keys = new[] { "valid1", null, "valid2", "", "nonexistent" };
        var result = await cache.GetAllAsync<int>(keys);

        // Should only return results for valid keys that exist
        Assert.Equal(3, result.Count);
        Assert.True(result.ContainsKey("valid1"));
        Assert.True(result.ContainsKey("valid2"));
        Assert.True(result.ContainsKey("nonexistent"));
        Assert.Equal(1, result["valid1"].Value);
        Assert.Equal(2, result["valid2"].Value);
        Assert.False(result["nonexistent"].HasValue);

        // Should not contain null or empty key entries
        Assert.DoesNotContain(result.Keys, k => String.IsNullOrEmpty(k));
    }

    public void Dispose()
    {
        _distributedCache.Dispose();
        _messageBus.Dispose();
    }
}
