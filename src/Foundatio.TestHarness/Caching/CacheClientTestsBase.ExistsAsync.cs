using System;
using System.Threading.Tasks;
using Foundatio.Caching;
using Xunit;

namespace Foundatio.Tests.Caching;

public abstract partial class CacheClientTestsBase
{
    public virtual async Task ExistsAsync_WithExistingKey_ReturnsTrue()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.SetAsync("test", 123);
            Assert.True(await cache.ExistsAsync("test"));
        }
    }

    public virtual async Task ExistsAsync_WithNonExistentKey_ReturnsFalse()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            Assert.False(await cache.ExistsAsync("nonexistent"));
        }
    }

    public virtual async Task ExistsAsync_WithNullStoredValue_ReturnsTrue()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

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
            await cache.SetAsync("test", 123, TimeSpan.FromMilliseconds(50));
            await Task.Delay(100);

            bool exists = await cache.ExistsAsync("test");

            Assert.False(exists);
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

    public virtual async Task ExistsAsync_AfterKeyExpires_ReturnsFalse()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.SetAsync("test", "value", TimeSpan.FromMilliseconds(100));
            Assert.True(await cache.ExistsAsync("test"));

            await Task.Delay(150);

            Assert.False(await cache.ExistsAsync("test"));
        }
    }

    public virtual async Task ExistsAsync_WithDifferentCasedKeys_ChecksExactMatch()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.SetAsync("orderId", "order123");

            bool lowerExists = await cache.ExistsAsync("orderId");
            bool titleExists = await cache.ExistsAsync("OrderId");
            bool upperExists = await cache.ExistsAsync("ORDERID");

            Assert.True(lowerExists);
            Assert.False(titleExists);
            Assert.False(upperExists);
        }
    }

    public virtual async Task ExistsAsync_WithNullKey_ThrowsArgumentNullException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await cache.ExistsAsync(null));
        }
    }

    public virtual async Task ExistsAsync_WithEmptyKey_ThrowsArgumentNullException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await cache.ExistsAsync(String.Empty));
        }
    }

    public virtual async Task ExistsAsync_WithWhitespaceKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await cache.ExistsAsync("   "));
        }
    }
}
