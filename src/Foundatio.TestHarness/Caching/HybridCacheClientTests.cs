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

public class HybridCacheClientTests : CacheClientTestsBase, IDisposable
{
    protected readonly ICacheClient _distributedCache;
    protected readonly IMessageBus _messageBus;

    public HybridCacheClientTests(ITestOutputHelper output) : base(output)
    {
        _distributedCache = new InMemoryCacheClient(o => o.LoggerFactory(Log));
        _messageBus = new InMemoryMessageBus(o => o.LoggerFactory(Log));
    }

    protected override ICacheClient GetCacheClient(bool shouldThrowOnSerializationError = true)
    {
        return new HybridCacheClient(_distributedCache, _messageBus, new InMemoryCacheClientOptions { CloneValues = true, ShouldThrowOnSerializationError = shouldThrowOnSerializationError, LoggerFactory = Log }, Log);
    }

    [Fact]
    public override Task CanGetAllAsync()
    {
        return base.CanGetAllAsync();
    }

    [Fact]
    public override Task CanGetAllWithOverlapAsync()
    {
        return base.CanGetAllWithOverlapAsync();
    }

    [Fact]
    public override Task CanSetAsync()
    {
        return base.CanSetAsync();
    }

    [Fact]
    public override Task CanSetAndGetValueAsync()
    {
        return base.CanSetAndGetValueAsync();
    }

    [Fact]
    public override Task CanAddAsync()
    {
        return base.CanAddAsync();
    }

    [Fact]
    public override Task CanAddConcurrentlyAsync()
    {
        return base.CanAddConcurrentlyAsync();
    }

    [Fact]
    public override Task CanTryGetAsync()
    {
        return base.CanTryGetAsync();
    }

    [Fact]
    public override Task CanUseScopedCachesAsync()
    {
        return base.CanUseScopedCachesAsync();
    }

    [Fact]
    public override Task CanSetAndGetObjectAsync()
    {
        return base.CanSetAndGetObjectAsync();
    }

    [Fact]
    public override Task CanRemoveAllAsync()
    {
        return base.CanRemoveAllAsync();
    }

    [Fact]
    public override Task CanRemoveAllKeysAsync()
    {
        return base.CanRemoveAllKeysAsync();
    }

    [Fact]
    public override Task CanRemoveByPrefixAsync()
    {
        return base.CanRemoveByPrefixAsync();
    }

    [Fact]
    public override Task CanRemoveByPrefixWithScopedCachesAsync()
    {
        return base.CanRemoveByPrefixWithScopedCachesAsync();
    }

    [Theory]
    [InlineData(50)]
    [InlineData(500)]
    public override Task CanRemoveByPrefixMultipleEntriesAsync(int count)
    {
        return base.CanRemoveByPrefixMultipleEntriesAsync(count);
    }

    [Fact]
    public override Task CanSetExpirationAsync()
    {
        return base.CanSetExpirationAsync();
    }

    [Fact]
    public override Task CanIncrementAsync()
    {
        return base.CanIncrementAsync();
    }

    [Fact]
    public override Task CanIncrementAndExpireAsync()
    {
        return base.CanIncrementAndExpireAsync();
    }

    [Fact]
    public override Task CanGetAndSetDateTimeAsync()
    {
        return base.CanGetAndSetDateTimeAsync();
    }

    [Fact]
    public override Task CanRoundTripLargeNumbersAsync()
    {
        return base.CanRoundTripLargeNumbersAsync();
    }

    [Fact]
    public override Task CanRoundTripLargeNumbersWithExpirationAsync()
    {
        return base.CanRoundTripLargeNumbersWithExpirationAsync();
    }

    [Fact]
    public override Task CanManageListsAsync()
    {
        return base.CanManageListsAsync();
    }

    [Fact]
    public override Task CanManageStringListsAsync()
    {
        return base.CanManageStringListsAsync();
    }

    [Fact]
    public override Task CanManageListPagingAsync()
    {
        return base.CanManageListPagingAsync();
    }

    [Fact]
    public override Task CanManageGetListExpirationAsync()
    {
        return base.CanManageGetListExpirationAsync();
    }

    [Fact]
    public override Task CanManageListAddExpirationAsync()
    {
        return base.CanManageListAddExpirationAsync();
    }

    [Fact]
    public override Task CanManageListRemoveExpirationAsync()
    {
        return base.CanManageListRemoveExpirationAsync();
    }

    [Fact]
    public virtual async Task WillUseLocalCache()
    {
        using var firstCache = GetCacheClient() as HybridCacheClient;
        Assert.NotNull(firstCache);

        using var secondCache = GetCacheClient() as HybridCacheClient;
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

    [Fact]
    public virtual async Task WillExpireRemoteItems()
    {
        using var firstCache = GetCacheClient() as HybridCacheClient;
        Assert.NotNull(firstCache);
        var firstResetEvent = new AsyncAutoResetEvent(false);

        void ExpiredHandler(object sender, ItemExpiredEventArgs args)
        {
            _logger.LogTrace("First local cache expired: {Key}", args.Key);
            firstResetEvent.Set();
        }

        using (firstCache.LocalCache.ItemExpired.AddSyncHandler(ExpiredHandler))
        {
            using var secondCache = GetCacheClient() as HybridCacheClient;
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

    [Fact]
    public virtual async Task WillWorkWithSets()
    {
        using var firstCache = GetCacheClient() as HybridCacheClient;
        Assert.NotNull(firstCache);

        using var secondCache = GetCacheClient() as HybridCacheClient;
        Assert.NotNull(secondCache);

        await firstCache.ListAddAsync("set1", new[] { 1, 2, 3 });

        var values = await secondCache.GetListAsync<int>("set1");

        Assert.Equal(3, values.Value.Count);
    }

    public void Dispose()
    {
        _distributedCache.Dispose();
        _messageBus.Dispose();
    }
}
