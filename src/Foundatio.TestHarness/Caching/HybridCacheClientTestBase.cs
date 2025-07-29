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
        _distributedCache = new InMemoryCacheClient(o => o.LoggerFactory(Log));
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

    public void Dispose()
    {
        _distributedCache.Dispose();
        _messageBus.Dispose();
    }
}
