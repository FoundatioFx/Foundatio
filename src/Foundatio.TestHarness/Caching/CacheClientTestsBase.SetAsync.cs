using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Foundatio.Caching;
using Xunit;

namespace Foundatio.Tests.Caching;

public abstract partial class CacheClientTestsBase
{
    public virtual async Task SetAsync_WithNullReferenceType_StoresAsNullValue()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            await cache.SetAsync<SimpleModel>("nullable", null);
            var nullCacheValue = await cache.GetAsync<SimpleModel>("nullable");
            Assert.True(nullCacheValue.HasValue);
            Assert.True(nullCacheValue.IsNull);
            Assert.True(await cache.ExistsAsync("nullable"));
        }
    }

    public virtual async Task SetAsync_WithNullValueType_StoresAsNullValue()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            Assert.False(await cache.ExistsAsync("nullableInt"));
            await cache.SetAsync<int?>("nullableInt", null);
            var nullIntCacheValue = await cache.GetAsync<int?>("nullableInt");
            Assert.True(nullIntCacheValue.HasValue);
            Assert.True(nullIntCacheValue.IsNull);
            Assert.True(await cache.ExistsAsync("nullableInt"));
        }
    }

    public virtual async Task SetAsync_WithDifferentScopes_IsolatesKeys()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var scopedCache1 = new ScopedCacheClient(cache, "scoped1");
            var scopedCache2 = new ScopedCacheClient(cache, "scoped2");

            await cache.SetAsync("test", 1);
            await scopedCache1.SetAsync("test", 2);
            await scopedCache2.SetAsync("test", 3);

            Assert.Equal(1, (await cache.GetAsync<int>("test")).Value);
            Assert.Equal(2, (await scopedCache1.GetAsync<int>("test")).Value);
            Assert.Equal(3, (await scopedCache2.GetAsync<int>("test")).Value);
        }
    }

    public virtual async Task SetAsync_WithNestedScopes_PreservesHierarchy()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var scopedCache1 = new ScopedCacheClient(cache, "scoped1");
            var nestedScopedCache1 = new ScopedCacheClient(scopedCache1, "nested");

            await cache.SetAsync("test", 1);
            await scopedCache1.SetAsync("test", 2);
            await nestedScopedCache1.SetAsync("test", 3);

            Assert.Equal(1, (await cache.GetAsync<int>("test")).Value);
            Assert.Equal(2, (await scopedCache1.GetAsync<int>("test")).Value);
            Assert.Equal(3, (await nestedScopedCache1.GetAsync<int>("test")).Value);

            Assert.Equal(3, (await scopedCache1.GetAsync<int>("nested:test")).Value);
            Assert.Equal(3, (await cache.GetAsync<int>("scoped1:nested:test")).Value);
        }
    }

    public virtual async Task SetAsync_WithComplexObject_StoresCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var dt = DateTimeOffset.Now;
            var value = new MyData { Type = "test", Date = dt, Message = "Hello World" };

            await cache.SetAsync("test", value);

            Assert.True(await cache.ExistsAsync("test"));
            var cachedValue = await cache.GetAsync<MyData>("test");
            Assert.NotNull(cachedValue);
            Assert.True(cachedValue.HasValue);
        }
    }

    public virtual async Task SetAsync_WithShortExpiration_ExpiresCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var expiresAt = DateTime.UtcNow.AddMilliseconds(300);
            bool success = await cache.SetAsync("test", 1, expiresAt);
            Assert.True(success);
            success = await cache.SetAsync("test2", 1, expiresAt.AddMilliseconds(100));
            Assert.True(success);
            Assert.Equal(1, (await cache.GetAsync<int>("test")).Value);
            Assert.True((await cache.GetExpirationAsync("test")).Value < TimeSpan.FromSeconds(1));

            await Task.Delay(500);
            Assert.False((await cache.GetAsync<int>("test")).HasValue);
            Assert.False((await cache.GetAsync<int>("test2")).HasValue);
        }
    }

    public virtual async Task SetAsync_WithExpirationEdgeCases_HandlesCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var utcNow = DateTime.UtcNow;
            var expires = DateTime.MaxValue - utcNow.AddDays(1);
            Assert.True(await cache.SetAsync("test1", 1, expires));
            Assert.Equal(1, (await cache.GetAsync<int>("test1")).Value);
            var actualExpiration = await cache.GetExpirationAsync("test1");
            Assert.NotNull(actualExpiration);
            Assert.InRange(actualExpiration.Value, expires.Subtract(TimeSpan.FromSeconds(10)), expires);

            // MinValue expires items.
            Assert.False(await cache.SetAsync("test2", 1, DateTime.MinValue));
            Assert.False(await cache.ExistsAsync("test2"));

            // MaxValue never expires.
            Assert.True(await cache.SetAsync("test3", 1, DateTime.MaxValue));
            Assert.Equal(1, (await cache.GetAsync<int>("test3")).Value);
            actualExpiration = await cache.GetExpirationAsync("test3");
            Assert.NotNull(actualExpiration);

            // Really high expiration value.
            Assert.True(await cache.SetAsync("test4", 1, DateTime.MaxValue - utcNow.AddDays(-1)));
            Assert.Equal(1, (await cache.GetAsync<int>("test4")).Value);
            actualExpiration = await cache.GetExpirationAsync("test4");
            Assert.NotNull(actualExpiration);

            // No Expiration
            Assert.True(await cache.SetAsync("test5", 1));
            Assert.Null(await cache.GetExpirationAsync("test5"));

            // Expire in an hour.
            var expiration = utcNow.AddHours(1);
            await cache.SetExpirationAsync("test5", expiration);
            actualExpiration = await cache.GetExpirationAsync("test5");
            Assert.NotNull(actualExpiration);
            Assert.InRange(actualExpiration.Value, expiration - expiration.Subtract(TimeSpan.FromSeconds(5)),
                expiration - utcNow);

            // Change expiration to MaxValue.
            await cache.SetExpirationAsync("test5", DateTime.MaxValue);
            Assert.NotNull(actualExpiration);

            // Change expiration to MinValue.
            await cache.SetExpirationAsync("test5", DateTime.MinValue);
            Assert.Null(await cache.GetExpirationAsync("test5"));
            Assert.False(await cache.ExistsAsync("test5"));

            // Ensure keys are not added as they are already expired
            Assert.Equal(0,
                await cache.SetAllAsync(
                    new Dictionary<string, object> { { "test6", 1 }, { "test7", 1 }, { "test8", 1 } },
                    DateTime.MinValue));

            // Expire time right now
            Assert.False(await cache.SetAsync("test9", 1, utcNow));
            Assert.False(await cache.ExistsAsync("test9"));
            Assert.Null(await cache.GetExpirationAsync("test9"));
        }
    }

    public virtual async Task SetAsync_WithLargeNumber_StoresCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            double value = 2 * 1000 * 1000 * 1000;
            Assert.True(await cache.SetAsync("test", value));
        }
    }

    public virtual async Task SetAsync_WithLargeNumbersAndExpiration_PreservesValues()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var minExpiration = TimeSpan.FromHours(1).Add(TimeSpan.FromMinutes(59)).Add(TimeSpan.FromSeconds(55));
            double value = 2 * 1000 * 1000 * 1000;
            Assert.True(await cache.SetAsync("test", value, TimeSpan.FromHours(2)));
            Assert.Equal(value, await cache.GetAsync<double>("test", 0));
            Assert.InRange((await cache.GetExpirationAsync("test")).Value, minExpiration, TimeSpan.FromHours(2));

            double lowerValue = value - 1000;
            Assert.Equal(1000, await cache.SetIfLowerAsync("test", lowerValue, TimeSpan.FromHours(2)));
            Assert.Equal(lowerValue, await cache.GetAsync<double>("test", 0));
            Assert.InRange((await cache.GetExpirationAsync("test")).Value, minExpiration, TimeSpan.FromHours(2));

            Assert.Equal(0, await cache.SetIfLowerAsync("test", value, TimeSpan.FromHours(2)));
            Assert.Equal(lowerValue, await cache.GetAsync<double>("test", 0));
            Assert.InRange((await cache.GetExpirationAsync("test")).Value, minExpiration, TimeSpan.FromHours(2));

            Assert.Equal(1000, await cache.SetIfHigherAsync("test", value, TimeSpan.FromHours(2)));
            Assert.Equal(value, await cache.GetAsync<double>("test", 0));
            Assert.InRange((await cache.GetExpirationAsync("test")).Value, minExpiration, TimeSpan.FromHours(2));

            Assert.Equal(0, await cache.SetIfHigherAsync("test", lowerValue, TimeSpan.FromHours(2)));
            Assert.Equal(value, await cache.GetAsync<double>("test", 0));
            Assert.InRange((await cache.GetExpirationAsync("test")).Value, minExpiration, TimeSpan.FromHours(2));
        }
    }

    public virtual async Task SetAsync_WithNullKey_ThrowsArgumentNullException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () => await cache.SetAsync(null, "value"));
        }
    }

    public virtual async Task SetAsync_WithEmptyKey_ThrowsArgumentNullException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () => await cache.SetAsync(String.Empty, "value"));
        }
    }

    public virtual async Task SetAsync_WithWhitespaceKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () => await cache.SetAsync("   ", "value"));
        }
    }

    public virtual async Task SetAsync_WithDifferentCasedKeys_CreatesDistinctEntries()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.SetAsync("productId", 100);
            await cache.SetAsync("ProductId", 200);
            await cache.SetAsync("PRODUCTID", 300);

            var lower = await cache.GetAsync<int>("productId");
            var title = await cache.GetAsync<int>("ProductId");
            var upper = await cache.GetAsync<int>("PRODUCTID");

            Assert.Equal(100, lower.Value);
            Assert.Equal(200, title.Value);
            Assert.Equal(300, upper.Value);
        }
    }

    public virtual async Task SetAsync_WithDifferentCasedScopes_MaintainsDistinctNamespaces()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            var scopedLower = new ScopedCacheClient(cache, "tenant");
            var scopedTitle = new ScopedCacheClient(cache, "Tenant");
            var scopedUpper = new ScopedCacheClient(cache, "TENANT");

            await scopedLower.SetAsync("dataId", "lower");
            await scopedTitle.SetAsync("dataId", "title");
            await scopedUpper.SetAsync("dataId", "upper");

            var lowerVal = await scopedLower.GetAsync<string>("dataId");
            var titleVal = await scopedTitle.GetAsync<string>("dataId");
            var upperVal = await scopedUpper.GetAsync<string>("dataId");

            Assert.Equal("lower", lowerVal.Value);
            Assert.Equal("title", titleVal.Value);
            Assert.Equal("upper", upperVal.Value);
        }
    }
}
