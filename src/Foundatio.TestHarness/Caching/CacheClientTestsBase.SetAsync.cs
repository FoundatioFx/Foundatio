using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Foundatio.Caching;
using Xunit;

namespace Foundatio.Tests.Caching;

public abstract partial class CacheClientTestsBase
{
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

            await cache.SetAsync("user:profile", value);

            Assert.True(await cache.ExistsAsync("user:profile"));
            var cachedValue = await cache.GetAsync<MyData>("user:profile");
            Assert.NotNull(cachedValue);
            Assert.True(cachedValue.HasValue);
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

    public virtual async Task SetAsync_WithInvalidKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () => await cache.SetAsync(null, "value"));
            await Assert.ThrowsAsync<ArgumentException>(async () => await cache.SetAsync(String.Empty, "value"));
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

    public virtual async Task SetAsync_WithNullValue_StoresAsNullValue()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            // Test null reference type
            await cache.SetAsync<SimpleModel>("nullable", null);
            var nullCacheValue = await cache.GetAsync<SimpleModel>("nullable");
            Assert.True(nullCacheValue.HasValue);
            Assert.True(nullCacheValue.IsNull);
            Assert.True(await cache.ExistsAsync("nullable"));

            // Test null value type
            Assert.False(await cache.ExistsAsync("nullableInt"));
            await cache.SetAsync<int?>("nullableInt", null);
            var nullIntCacheValue = await cache.GetAsync<int?>("nullableInt");
            Assert.True(nullIntCacheValue.HasValue);
            Assert.True(nullIntCacheValue.IsNull);
            Assert.True(await cache.ExistsAsync("nullableInt"));
        }
    }

    public virtual async Task SetAsync_WithScopedCaches_IsolatesKeys()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            // Test different scopes isolate keys
            var scopedCache1 = new ScopedCacheClient(cache, "scoped1");
            var scopedCache2 = new ScopedCacheClient(cache, "scoped2");

            await cache.SetAsync("test", 1);
            await scopedCache1.SetAsync("test", 2);
            await scopedCache2.SetAsync("test", 3);

            Assert.Equal(1, (await cache.GetAsync<int>("test")).Value);
            Assert.Equal(2, (await scopedCache1.GetAsync<int>("test")).Value);
            Assert.Equal(3, (await scopedCache2.GetAsync<int>("test")).Value);

            // Test nested scopes preserve hierarchy
            var nestedScopedCache1 = new ScopedCacheClient(scopedCache1, "nested");
            await nestedScopedCache1.SetAsync("test", 4);

            Assert.Equal(4, (await nestedScopedCache1.GetAsync<int>("test")).Value);
            Assert.Equal(4, (await scopedCache1.GetAsync<int>("nested:test")).Value);
            Assert.Equal(4, (await cache.GetAsync<int>("scoped1:nested:test")).Value);

            // Test case-sensitive scopes maintain distinct namespaces
            var scopedLower = new ScopedCacheClient(cache, "tenant");
            var scopedTitle = new ScopedCacheClient(cache, "Tenant");
            var scopedUpper = new ScopedCacheClient(cache, "TENANT");

            await scopedLower.SetAsync("dataId", "lower");
            await scopedTitle.SetAsync("dataId", "title");
            await scopedUpper.SetAsync("dataId", "upper");

            Assert.Equal("lower", (await scopedLower.GetAsync<string>("dataId")).Value);
            Assert.Equal("title", (await scopedTitle.GetAsync<string>("dataId")).Value);
            Assert.Equal("upper", (await scopedUpper.GetAsync<string>("dataId")).Value);

            // Test case-sensitive keys create distinct entries
            await cache.SetAsync("productId", 100);
            await cache.SetAsync("ProductId", 200);
            await cache.SetAsync("PRODUCTID", 300);

            Assert.Equal(100, (await cache.GetAsync<int>("productId")).Value);
            Assert.Equal(200, (await cache.GetAsync<int>("ProductId")).Value);
            Assert.Equal(300, (await cache.GetAsync<int>("PRODUCTID")).Value);
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

            var expiresAt = DateTime.UtcNow.AddMilliseconds(100);
            bool success = await cache.SetAsync("test", 1, expiresAt);
            Assert.True(success);
            success = await cache.SetAsync("test2", 1, expiresAt.AddMilliseconds(50));
            Assert.True(success);
            Assert.Equal(1, (await cache.GetAsync<int>("test")).Value);
            Assert.True((await cache.GetExpirationAsync("test")).Value < TimeSpan.FromSeconds(1));

            await Task.Delay(200);
            Assert.False((await cache.GetAsync<int>("test")).HasValue);
            Assert.False((await cache.GetAsync<int>("test2")).HasValue);
        }
    }
}
