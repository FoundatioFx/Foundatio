using System;
using System.Threading.Tasks;
using Foundatio.Caching;
using Xunit;

namespace Foundatio.Tests.Caching;

public abstract partial class CacheClientTestsBase
{
    public virtual async Task GetAsync_WithNonExistentKey_ReturnsNoValue()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            Assert.False((await cache.GetAsync<int>("donkey")).HasValue);
            Assert.False(await cache.ExistsAsync("donkey"));
        }
    }

    public virtual async Task GetAsync_WithNumericTypeConversion_ConvertsIntToLong()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            await cache.SetAsync("test", 1);
            var cacheValue = await cache.GetAsync<long>("test");
            Assert.True(cacheValue.HasValue);
            Assert.Equal(1L, cacheValue.Value);
        }
    }

    public virtual async Task GetAsync_WithNumericTypeConversion_ConvertsLongToInt()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            await cache.SetAsync<long>("test", 1);
            var cacheValue = await cache.GetAsync<int>("test");
            Assert.True(cacheValue.HasValue);
            Assert.Equal(1L, cacheValue.Value);
        }
    }

    public virtual async Task GetAsync_WithMaxLongAsInt_ThrowsException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            await cache.SetAsync("test", Int64.MaxValue);
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                var cacheValue = await cache.GetAsync<int>("test");
                Assert.False(cacheValue.HasValue);
            });

            var cacheValue2 = await cache.GetAsync<long>("test");
            Assert.True(cacheValue2.HasValue);
            Assert.Equal(Int64.MaxValue, cacheValue2.Value);
        }
    }

    public virtual async Task GetAsync_WithTryGetSemanticsAndIntAsLong_ConvertsSuccessfully()
    {
        var cache = GetCacheClient(false);
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            await cache.SetAsync("test", 1);
            var cacheValue = await cache.GetAsync<long>("test");
            Assert.True(cacheValue.HasValue);
            Assert.Equal(1L, cacheValue.Value);

            await cache.SetAsync<long>("test", 1);
            var cacheValue2 = await cache.GetAsync<int>("test");
            Assert.True(cacheValue2.HasValue);
            Assert.Equal(1L, cacheValue2.Value);
        }
    }

    public virtual async Task GetAsync_WithTryGetSemanticsAndMaxLongAsInt_ReturnsNoValue()
    {
        var cache = GetCacheClient(false);
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            await cache.SetAsync("test", Int64.MaxValue);
            var cacheValue3 = await cache.GetAsync<int>("test");
            Assert.False(cacheValue3.HasValue);

            var cacheValue = await cache.GetAsync<long>("test");
            Assert.True(cacheValue.HasValue);
            Assert.Equal(Int64.MaxValue, cacheValue.Value);
        }
    }

    public virtual async Task GetAsync_WithTryGetSemanticsAndComplexTypeAsLong_ReturnsNoValue()
    {
        var cache = GetCacheClient(false);
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            await cache.SetAsync("test", new MyData { Message = "test" });
            var cacheValue = await cache.GetAsync<long>("test");
            Assert.False(cacheValue.HasValue);
        }
    }

    public virtual async Task GetAsync_WithComplexObject_ReturnsNewInstance(string cacheKey)
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var value = new MyData { Type = "test", Date = DateTimeOffset.Now, Message = "Hello World" };

            await cache.SetAsync(cacheKey, value);
            value.Type = "modified";

            var cachedValue = await cache.GetAsync<MyData>(cacheKey);
            Assert.NotNull(cachedValue);
            Assert.False(value.Equals(cachedValue.Value), "Should not be same reference object");
            Assert.Equal("test", cachedValue.Value.Type);
            Assert.NotEqual("modified", cachedValue.Value.Type);
        }
    }

    public virtual async Task GetAsync_WithComplexObject_PreservesAllProperties()
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
            var cachedValue = await cache.GetAsync<MyData>("test");

            Assert.NotNull(cachedValue);
            Assert.Equal("test", cachedValue.Value.Type);
            Assert.Equal(dt, cachedValue.Value.Date);
            Assert.Equal("Hello World", cachedValue.Value.Message);
        }
    }

    public virtual async Task GetAsync_WithLargeNumber_ReturnsCorrectValue()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            double value = 2 * 1000 * 1000 * 1000;
            await cache.SetAsync("test", value);
            Assert.Equal(value, await cache.GetAsync<double>("test", 0));
        }
    }

    public virtual async Task GetAsync_WithNullKey_ThrowsArgumentNullException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () => await cache.GetAsync<string>(null));
        }
    }

    public virtual async Task GetAsync_WithEmptyKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentException>(async () => await cache.GetAsync<string>(String.Empty));
        }
    }

    public virtual async Task GetAsync_WithDifferentCasedKeys_TreatsAsDifferentKeys()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.SetAsync("userId", "lowercase");
            await cache.SetAsync("UserId", "titlecase");
            await cache.SetAsync("USERID", "uppercase");

            var lower = await cache.GetAsync<string>("userId");
            var title = await cache.GetAsync<string>("UserId");
            var upper = await cache.GetAsync<string>("USERID");

            Assert.Equal("lowercase", lower.Value);
            Assert.Equal("titlecase", title.Value);
            Assert.Equal("uppercase", upper.Value);
        }
    }
}
