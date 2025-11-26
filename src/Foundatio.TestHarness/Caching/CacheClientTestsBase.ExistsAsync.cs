using System;
using System.Threading.Tasks;
using Foundatio.Caching;
using Xunit;

namespace Foundatio.Tests.Caching;

public abstract partial class CacheClientTestsBase
{
    public virtual async Task ExistsAsync_WithVariousKeys_ReturnsCorrectExistenceStatus()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            // Non-existent key returns false
            Assert.False(await cache.ExistsAsync("nonexistent"));

            // Existing key returns true
            await cache.SetAsync("test", 123);
            Assert.True(await cache.ExistsAsync("test"));

            // Case-sensitivity check
            await cache.SetAsync("orderId", "order123");
            Assert.True(await cache.ExistsAsync("orderId"));
            Assert.False(await cache.ExistsAsync("OrderId"));
            Assert.False(await cache.ExistsAsync("ORDERID"));

            // Null stored value still exists
            SimpleModel nullable = null;
            await cache.SetAsync("nullable", nullable);
            Assert.True(await cache.ExistsAsync("nullable"));

            int? nullableInt = null;
            await cache.SetAsync("nullableInt", nullableInt);
            Assert.True(await cache.ExistsAsync("nullableInt"));
        }
    }

    public virtual async Task ExistsAsync_WithExpiredKey_ReturnsFalse()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.SetAsync("test", "value", TimeSpan.FromMilliseconds(50));
            Assert.True(await cache.ExistsAsync("test"));

            await Task.Delay(100);

            Assert.False(await cache.ExistsAsync("test"));
        }
    }

    public virtual async Task ExistsAsync_WithScopedCache_ChecksOnlyWithinScope()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            var scopedCache1 = new ScopedCacheClient(cache, "scope1");
            var scopedCache2 = new ScopedCacheClient(cache, "scope2");

            await scopedCache1.SetAsync("test", 1);
            await scopedCache2.SetAsync("test", 2);

            Assert.True(await scopedCache1.ExistsAsync("test"));
            Assert.True(await scopedCache2.ExistsAsync("test"));
            Assert.False(await cache.ExistsAsync("test"));
        }
    }

    public virtual async Task ExistsAsync_WithInvalidKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await cache.ExistsAsync(null));

            await Assert.ThrowsAsync<ArgumentException>(async () =>
                await cache.ExistsAsync(String.Empty));
        }
    }
}
