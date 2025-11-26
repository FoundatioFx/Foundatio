using System;
using System.Threading.Tasks;
using Foundatio.Caching;
using Xunit;

namespace Foundatio.Tests.Caching;

public abstract partial class CacheClientTestsBase
{
    public virtual async Task GetAsync_WithNumericTypeConversion_ConvertsBetweenTypes()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            // int to long
            await cache.SetAsync("intValue", 1);
            var longResult = await cache.GetAsync<long>("intValue");
            Assert.True(longResult.HasValue);
            Assert.Equal(1L, longResult.Value);

            // long to int
            await cache.SetAsync<long>("longValue", 1);
            var intResult = await cache.GetAsync<int>("longValue");
            Assert.True(intResult.HasValue);
            Assert.Equal(1, intResult.Value);

            // large double
            double largeValue = 2 * 1000 * 1000 * 1000;
            await cache.SetAsync("largeValue", largeValue);
            Assert.Equal(largeValue, await cache.GetAsync<double>("largeValue", 0));

            // overflow throws exception when shouldThrowOnSerializationError is true (default)
            await cache.SetAsync("maxLong", Int64.MaxValue);
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                var cacheValue = await cache.GetAsync<int>("maxLong");
                Assert.False(cacheValue.HasValue);
            });

            var validLongResult = await cache.GetAsync<long>("maxLong");
            Assert.True(validLongResult.HasValue);
            Assert.Equal(Int64.MaxValue, validLongResult.Value);
        }
    }

    public virtual async Task GetAsync_WithTryGetSemantics_HandlesTypeConversions()
    {
        var cache = GetCacheClient(false);
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            // Successful conversions
            await cache.SetAsync("intValue", 1);
            var longResult = await cache.GetAsync<long>("intValue");
            Assert.True(longResult.HasValue);
            Assert.Equal(1L, longResult.Value);

            await cache.SetAsync<long>("longValue", 1);
            var intResult = await cache.GetAsync<int>("longValue");
            Assert.True(intResult.HasValue);
            Assert.Equal(1, intResult.Value);

            // Overflow returns no value instead of throwing
            await cache.SetAsync("maxLong", Int64.MaxValue);
            var overflowResult = await cache.GetAsync<int>("maxLong");
            Assert.False(overflowResult.HasValue);

            var validLongResult = await cache.GetAsync<long>("maxLong");
            Assert.True(validLongResult.HasValue);
            Assert.Equal(Int64.MaxValue, validLongResult.Value);

            // Complex type as primitive returns no value
            await cache.SetAsync("complex", new MyData { Message = "test" });
            var complexAsLong = await cache.GetAsync<long>("complex");
            Assert.False(complexAsLong.HasValue);
        }
    }

    public virtual async Task GetAsync_WithComplexObject_PreservesPropertiesAndReturnsNewInstance()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            // Non-existent key returns no value
            Assert.False((await cache.GetAsync<MyData>("non-existent-key")).HasValue);
            Assert.False(await cache.ExistsAsync("non-existent-key"));

            var dt = DateTimeOffset.Now;
            var value = new MyData { Type = "test", Date = dt, Message = "Hello World" };

            await cache.SetAsync("order:details", value);
            value.Type = "modified";

            var cachedValue = await cache.GetAsync<MyData>("order:details");
            Assert.NotNull(cachedValue);
            Assert.False(value.Equals(cachedValue.Value), "Should not be same reference object");
            Assert.Equal("test", cachedValue.Value.Type);
            Assert.NotEqual("modified", cachedValue.Value.Type);
            Assert.Equal(dt, cachedValue.Value.Date);
            Assert.Equal("Hello World", cachedValue.Value.Message);

            // Verify case sensitivity - different cased keys should be treated as distinct
            await cache.SetAsync("userId", new MyData { Type = "lowercase", Date = dt, Message = "Lower" });
            await cache.SetAsync("USERID", new MyData { Type = "uppercase", Date = dt, Message = "Upper" });

            var lowerResult = await cache.GetAsync<MyData>("userId");
            var upperResult = await cache.GetAsync<MyData>("USERID");

            Assert.True(lowerResult.HasValue);
            Assert.True(upperResult.HasValue);
            Assert.Equal("lowercase", lowerResult.Value.Type);
            Assert.Equal("uppercase", upperResult.Value.Type);
        }
    }

    public virtual async Task GetAsync_WithInvalidKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () => await cache.GetAsync<string>(null));
            await Assert.ThrowsAsync<ArgumentException>(async () => await cache.GetAsync<string>(String.Empty));
        }
    }
}
