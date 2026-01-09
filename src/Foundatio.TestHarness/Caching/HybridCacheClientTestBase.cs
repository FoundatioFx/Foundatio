using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Foundatio.AsyncEx;
using Foundatio.Caching;
using Foundatio.Messaging;
using Foundatio.Tests.Extensions;
using Microsoft.Extensions.Logging;
using Xunit;

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

    public virtual async Task AddAsync_WithExpiration_ExpiresRemoteItems()
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

    public virtual async Task ExistsAsync_WithLocalCache_ChecksLocalCacheFirst()
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

    public virtual async Task GetAllAsync_WithMultipleKeys_UsesHybridCache()
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

    public virtual async Task GetExpirationAsync_WithLocalCache_ChecksLocalCacheFirst()
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

    public virtual async Task IncrementAsync_WithMultipleInstances_InvalidatesOtherClientLocalCache()
    {
        using var firstCache = GetDistributedHybridCacheClient();
        Assert.NotNull(firstCache);

        using var secondCache = GetDistributedHybridCacheClient();
        Assert.NotNull(secondCache);

        const string cacheKey = "increment-invalidate-test";

        // Arrange: Client A sets key to 10
        await firstCache.SetAsync(cacheKey, 10L);
        Assert.Equal(1, firstCache.LocalCache.Count);

        // Client B reads key (populates B's local cache with 10)
        var result = await secondCache.GetAsync<long>(cacheKey);
        Assert.True(result.HasValue);
        Assert.Equal(10L, result.Value);
        Assert.Equal(1, secondCache.LocalCache.Count);

        // Act: Client A increments by 5
        var newValue = await firstCache.IncrementAsync(cacheKey, 5L);
        Assert.Equal(15L, newValue);

        // Wait for invalidation message to propagate
        await Task.Delay(250);

        // Assert: Client B's local cache should be invalidated
        Assert.Equal(0, secondCache.LocalCache.Count);

        // Client B reads key - should get 15 (not stale 10)
        result = await secondCache.GetAsync<long>(cacheKey);
        Assert.True(result.HasValue);
        Assert.Equal(15L, result.Value);

        // Test zero expiration: should remove key from both caches
        var zeroExpResult = await firstCache.IncrementAsync(cacheKey, 5L, TimeSpan.Zero);
        Assert.Equal(0, zeroExpResult);
        Assert.Equal(0, firstCache.LocalCache.Count);

        await Task.Delay(250); // Allow invalidation to propagate
        Assert.Equal(0, secondCache.LocalCache.Count);
        Assert.False(await firstCache.ExistsAsync(cacheKey));
        Assert.False(await secondCache.ExistsAsync(cacheKey));
    }

    public virtual async Task ListAddAsync_WithMultipleInstances_WorksCorrectly()
    {
        using var firstCache = GetDistributedHybridCacheClient();
        Assert.NotNull(firstCache);

        using var secondCache = GetDistributedHybridCacheClient();
        Assert.NotNull(secondCache);

        await firstCache.ListAddAsync("set1", [1, 2, 3]);
        var values = await secondCache.GetListAsync<int>("set1");
        Assert.Equal(3, values.Value.Count);
    }

    public virtual async Task RemoveAllAsync_WithLocalCache_InvalidatesLocalCache()
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

    public virtual async Task RemoveByPrefixAsync_WithLocalCache_InvalidatesLocalCache()
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

    public virtual async Task RemoveIfEqualAsync_WithMultipleInstances_InvalidatesOtherClientLocalCache()
    {
        using var firstCache = GetDistributedHybridCacheClient();
        Assert.NotNull(firstCache);

        using var secondCache = GetDistributedHybridCacheClient();
        Assert.NotNull(secondCache);

        const string cacheKey = "remove-if-equal-invalidate-test";

        // Arrange: Client A sets key to "value"
        await firstCache.SetAsync(cacheKey, "value");
        Assert.Equal(1, firstCache.LocalCache.Count);

        // Client B reads key (populates B's local cache)
        var result = await secondCache.GetAsync<string>(cacheKey);
        Assert.True(result.HasValue);
        Assert.Equal("value", result.Value);
        Assert.Equal(1, secondCache.LocalCache.Count);

        // Act: Client A removes key (expected="value")
        Assert.True(await firstCache.RemoveIfEqualAsync(cacheKey, "value"));

        // Wait for invalidation message to propagate
        await Task.Delay(250);

        // Assert: Client B's local cache should be invalidated
        Assert.Equal(0, secondCache.LocalCache.Count);

        // Client B reads key - should get NoValue
        result = await secondCache.GetAsync<string>(cacheKey);
        Assert.False(result.HasValue);
    }

    public virtual async Task ReplaceIfEqualAsync_WithMultipleInstances_InvalidatesOtherClientLocalCache()
    {
        using var firstCache = GetDistributedHybridCacheClient();
        Assert.NotNull(firstCache);

        using var secondCache = GetDistributedHybridCacheClient();
        Assert.NotNull(secondCache);

        const string cacheKey = "replace-if-equal-invalidate-test";

        // Arrange: Client A sets key to "original"
        await firstCache.SetAsync(cacheKey, "original");
        Assert.Equal(1, firstCache.LocalCache.Count);

        // Client B reads key (populates B's local cache)
        var result = await secondCache.GetAsync<string>(cacheKey);
        Assert.True(result.HasValue);
        Assert.Equal("original", result.Value);
        Assert.Equal(1, secondCache.LocalCache.Count);

        // Act: Client A replaces key with "new" (expected="original")
        Assert.True(await firstCache.ReplaceIfEqualAsync(cacheKey, "new", "original"));

        // Wait for invalidation message to propagate
        await Task.Delay(250);

        // Assert: Client B's local cache should be invalidated
        Assert.Equal(0, secondCache.LocalCache.Count);

        // Client B reads key - should get "new"
        result = await secondCache.GetAsync<string>(cacheKey);
        Assert.True(result.HasValue);
        Assert.Equal("new", result.Value);
    }

    public virtual async Task SetAsync_WithMultipleInstances_InvalidatesOtherClientLocalCache()
    {
        using var firstCache = GetDistributedHybridCacheClient();
        Assert.NotNull(firstCache);

        using var secondCache = GetDistributedHybridCacheClient();
        Assert.NotNull(secondCache);

        const string cacheKey = "set-invalidate-test";

        // Arrange: Client A sets key to "value1"
        await firstCache.SetAsync(cacheKey, "value1");
        Assert.Equal(1, firstCache.LocalCache.Count);

        // Client B reads key (populates B's local cache with "value1")
        var result = await secondCache.GetAsync<string>(cacheKey);
        Assert.True(result.HasValue);
        Assert.Equal("value1", result.Value);
        Assert.Equal(1, secondCache.LocalCache.Count);

        // Act: Client A sets key to "value2" (should invalidate B's local cache)
        await firstCache.SetAsync(cacheKey, "value2");

        // Wait for invalidation message to propagate
        await Task.Delay(250);

        // Assert: Client B's local cache should be invalidated
        Assert.Equal(0, secondCache.LocalCache.Count);

        // Client B reads key - should get "value2" (not stale "value1")
        result = await secondCache.GetAsync<string>(cacheKey);
        Assert.True(result.HasValue);
        Assert.Equal("value2", result.Value);
    }

    public virtual async Task SetAsync_WithMultipleInstances_UsesLocalCache()
    {
        using var firstCache = GetDistributedHybridCacheClient();
        Assert.NotNull(firstCache);

        using var secondCache = GetDistributedHybridCacheClient();
        Assert.NotNull(secondCache);

        await firstCache.SetAsync("first1", 1);
        await firstCache.IncrementAsync("first2");
        Assert.Equal(2, firstCache.LocalCache.Count); // Both SetAsync and IncrementAsync populate local cache

        string cacheKey = Guid.NewGuid().ToString("N").Substring(10);
        await firstCache.SetAsync(cacheKey, new SimpleModel { Data1 = "test" });
        Assert.Equal(3, firstCache.LocalCache.Count);
        Assert.Equal(0, secondCache.LocalCache.Count);
        Assert.Equal(0, firstCache.LocalCacheHits);

        Assert.True((await firstCache.GetAsync<SimpleModel>(cacheKey)).HasValue);
        Assert.Equal(1, firstCache.LocalCacheHits);
        Assert.Equal(3, firstCache.LocalCache.Count);

        Assert.True((await secondCache.GetAsync<SimpleModel>(cacheKey)).HasValue);
        Assert.Equal(0, secondCache.LocalCacheHits);
        Assert.Equal(1, secondCache.LocalCache.Count);

        Assert.True((await secondCache.GetAsync<SimpleModel>(cacheKey)).HasValue);
        Assert.Equal(1, secondCache.LocalCacheHits);
    }

    public void Dispose()
    {
        _distributedCache.Dispose();
        _messageBus.Dispose();
    }
}
