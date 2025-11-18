using System;
using System.Threading.Tasks;
using Foundatio.Caching;
using Xunit;

namespace Foundatio.Tests.Caching;

public abstract partial class CacheClientTestsBase
{
    public virtual async Task IncrementAsync_WithScopedCache_WorksWithinScope()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var scopedCache1 = new ScopedCacheClient(cache, "scoped1");
            var nestedScopedCache1 = new ScopedCacheClient(scopedCache1, "nested");

            Assert.Equal(0, await scopedCache1.GetAsync<double>("total", 0));
            Assert.Equal(10, await scopedCache1.IncrementAsync("total", 10));
            Assert.Equal(10, await scopedCache1.GetAsync<double>("total", 0));

            Assert.Equal(0, await nestedScopedCache1.GetAsync<double>("total", 0));
            Assert.Equal(20, await nestedScopedCache1.IncrementAsync("total", 20));
            Assert.Equal(20, await nestedScopedCache1.GetAsync<double>("total", 0));
            Assert.Equal(1, await nestedScopedCache1.RemoveAllAsync(["id", "total"]));
            Assert.Equal(0, await nestedScopedCache1.GetAsync<double>("total", 0));

            Assert.Equal(1, await scopedCache1.RemoveAllAsync(["id", "total"]));
            Assert.Equal(0, await scopedCache1.GetAsync<double>("total", 0));
        }
    }

    public virtual async Task IncrementAsync_WithExistingKey_IncrementsValue()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            Assert.True(await cache.SetAsync("test", 0));
            Assert.Equal(1, await cache.IncrementAsync("test"));
        }
    }

    public virtual async Task IncrementAsync_WithNonExistentKey_InitializesToOne()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            Assert.Equal(1, await cache.IncrementAsync("test1"));
        }
    }

    public virtual async Task IncrementAsync_WithSpecifiedAmount_IncrementsCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            Assert.Equal(0, await cache.IncrementAsync("test3", 0));
        }
    }

    public virtual async Task IncrementAsync_WithExpiration_ExpiresCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            bool success = await cache.SetAsync("test", 0);
            Assert.True(success);

            var expiresIn = TimeSpan.FromSeconds(1);
            double newVal = await cache.IncrementAsync("test", 1, expiresIn);

            Assert.Equal(1, newVal);

            await Task.Delay(1500);
            Assert.False((await cache.GetAsync<int>("test")).HasValue);
        }
    }

    public virtual async Task IncrementAsync_WithNullKey_ThrowsArgumentNullException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () => await cache.IncrementAsync(null, 1));
        }
    }

    public virtual async Task IncrementAsync_WithEmptyKey_ThrowsArgumentNullException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () => await cache.IncrementAsync(String.Empty, 1));
        }
    }

    public virtual async Task IncrementAsync_WithWhitespaceKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () => await cache.IncrementAsync("   ", 1));
        }
    }

    public virtual async Task IncrementAsync_WithDifferentCasedKeys_IncrementsDistinctCounters()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            long lower = await cache.IncrementAsync("counter", 1);
            long title = await cache.IncrementAsync("Counter", 2);
            long upper = await cache.IncrementAsync("COUNTER", 3);

            Assert.Equal(1, lower);
            Assert.Equal(2, title);
            Assert.Equal(3, upper);

            var lowerFinal = await cache.GetAsync<long>("counter");
            var titleFinal = await cache.GetAsync<long>("Counter");
            var upperFinal = await cache.GetAsync<long>("COUNTER");

            Assert.Equal(1, lowerFinal.Value);
            Assert.Equal(2, titleFinal.Value);
            Assert.Equal(3, upperFinal.Value);
        }
    }
}
