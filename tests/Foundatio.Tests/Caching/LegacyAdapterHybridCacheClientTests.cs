using System;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Foundatio.Tests.Caching;

/// <summary>Runs the hybrid cache suite with invalidations flowing through the legacy bus adapter over the new transport.</summary>
public class LegacyAdapterHybridCacheClientTests : InMemoryHybridCacheClientTests
{
    private readonly IMessageBus _adapter;

    public LegacyAdapterHybridCacheClientTests(ITestOutputHelper output) : base(output)
    {
        _adapter = new LegacyMessageBusAdapter(new InMemoryMessageTransport(), new SharedMessageBusOptions { LoggerFactory = Log });
    }

    protected override ICacheClient GetCacheClient(bool shouldThrowOnSerializationError = true)
    {
        return new InMemoryHybridCacheClient(_adapter, Log, shouldThrowOnSerializationError);
    }

    protected override HybridCacheClient GetDistributedHybridCacheClient(bool shouldThrowOnSerializationError = true)
    {
        return new InMemoryHybridCacheClient(_distributedCache, _adapter, Log, shouldThrowOnSerializationError);
    }

    /// <summary>
    /// The inherited test gives the item a 100 ms lifetime on the real clock and then reads it, which fails whenever
    /// the machine is busy for 100 ms. The same steps run here on a fake clock, so the item expires exactly when the
    /// clock is moved past its lifetime.
    /// </summary>
    [Fact]
    public override async Task GetListAsync_WithExpiredItems_RemovesExpiredAndReturnsActive()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var distributedCache = new InMemoryCacheClient(o => o.CloneValues(true).TimeProvider(timeProvider).LoggerFactory(Log));
        using var cache = new HybridCacheClient(distributedCache, _adapter, new InMemoryCacheClientOptions { CloneValues = true, TimeProvider = timeProvider, LoggerFactory = Log }, Log);
        await cache.RemoveAllAsync();
        const string key = "list:expiration";

        Assert.Equal(1, await cache.ListAddAsync(key, [1], TimeSpan.FromMilliseconds(100)));

        var cacheValue = await cache.GetListAsync<int>(key);
        Assert.True(cacheValue.HasValue);
        Assert.Single(cacheValue.Value);
        Assert.Contains(1, cacheValue.Value);

        timeProvider.Advance(TimeSpan.FromMilliseconds(150));

        cacheValue = await cache.GetListAsync<int>(key);
        Assert.False(cacheValue.HasValue);
        Assert.False(await cache.ExistsAsync(key));
    }

    /// <summary>
    /// The inherited test assumes the invalidation from the first client's set arrives before the second client reads,
    /// and that 250 ms is enough for the later ones; through the adapter, a busy machine delivers them later. The same
    /// steps run here, each waiting until the second client has received the invalidation it depends on.
    /// </summary>
    [Fact]
    public override async Task IncrementAsync_WithMultipleInstances_InvalidatesOtherClientLocalCache()
    {
        using var firstCache = GetDistributedHybridCacheClient();
        using var secondCache = GetDistributedHybridCacheClient();
        const string cacheKey = "increment-invalidate-test";
        Assert.False(await secondCache.ExistsAsync(cacheKey));

        await firstCache.SetAsync(cacheKey, 10L);
        Assert.Equal(1, firstCache.LocalCache.Count);
        await WaitForInvalidationsAsync(secondCache, 1);

        var result = await secondCache.GetAsync<long>(cacheKey);
        Assert.True(result.HasValue);
        Assert.Equal(10L, result.Value);
        Assert.Equal(1, secondCache.LocalCache.Count);

        long newValue = await firstCache.IncrementAsync(cacheKey, 5L);
        Assert.Equal(15L, newValue);
        await WaitForInvalidationsAsync(secondCache, 2);

        Assert.Equal(0, secondCache.LocalCache.Count);
        result = await secondCache.GetAsync<long>(cacheKey);
        Assert.True(result.HasValue);
        Assert.Equal(15L, result.Value);

        long zeroExpResult = await firstCache.IncrementAsync(cacheKey, 5L, TimeSpan.Zero);
        Assert.Equal(0, zeroExpResult);
        Assert.Equal(0, firstCache.LocalCache.Count);
        await WaitForInvalidationsAsync(secondCache, 3);

        Assert.Equal(0, secondCache.LocalCache.Count);
        Assert.False(await firstCache.ExistsAsync(cacheKey));
        Assert.False(await secondCache.ExistsAsync(cacheKey));
    }

    private async Task WaitForInvalidationsAsync(HybridCacheClient cache, long count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (cache.InvalidateCacheCalls < count)
        {
            Assert.True(DateTime.UtcNow < deadline, $"Only {cache.InvalidateCacheCalls} of {count} invalidations arrived.");
            await Task.Delay(10, TestCancellationToken);
        }
    }
}
