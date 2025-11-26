using System;
using System.Threading.Tasks;
using Foundatio.Caching;
using Xunit;

namespace Foundatio.Tests.Caching;

public abstract partial class CacheClientTestsBase
{
    public virtual async Task IncrementAsync_WithExpiration_ExpiresCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            Assert.True(await cache.SetAsync("increment-expiration-test", 0));

            double newVal = await cache.IncrementAsync("increment-expiration-test", 1, TimeSpan.FromMilliseconds(50));
            Assert.Equal(1, newVal);

            await Task.Delay(100);
            Assert.False((await cache.GetAsync<int>("increment-expiration-test")).HasValue);
        }
    }

    public virtual async Task IncrementAsync_WithInvalidKey_ThrowsException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () => await cache.IncrementAsync(null, 1));
            await Assert.ThrowsAsync<ArgumentException>(async () => await cache.IncrementAsync(String.Empty, 1));
        }
    }

    public virtual async Task IncrementAsync_WithKey_IncrementsCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            // Non-existent key with default amount initializes to 1 (also tests case-sensitivity)
            Assert.Equal(1, await cache.IncrementAsync("counter"));
            Assert.Equal(5, await cache.IncrementAsync("Counter", 5));
            Assert.Equal(0, await cache.IncrementAsync("COUNTER", 0));

            // Increment existing key
            Assert.Equal(2, await cache.IncrementAsync("counter"));

            // Verify all three case-sensitive keys have correct values
            Assert.Equal(2, (await cache.GetAsync<long>("counter")).Value);
            Assert.Equal(5, (await cache.GetAsync<long>("Counter")).Value);
            Assert.Equal(0, (await cache.GetAsync<long>("COUNTER")).Value);
        }
    }

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
}
