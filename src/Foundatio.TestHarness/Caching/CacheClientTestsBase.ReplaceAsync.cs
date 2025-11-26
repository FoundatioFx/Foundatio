using System;
using System.Threading.Tasks;
using Xunit;

namespace Foundatio.Tests.Caching;

public abstract partial class CacheClientTestsBase
{
    public virtual async Task ReplaceAsync_WithExistingKey_ReturnsTrueAndReplacesValue()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            const string cacheKey = "replace-existing";

            // Add initial value
            Assert.True(await cache.AddAsync(cacheKey, "original"));
            var result = await cache.GetAsync<string>(cacheKey);
            Assert.NotNull(result);
            Assert.Equal("original", result.Value);

            // Replace value without expiration
            Assert.True(await cache.ReplaceAsync(cacheKey, "replaced"));
            result = await cache.GetAsync<string>(cacheKey);
            Assert.NotNull(result);
            Assert.Equal("replaced", result.Value);

            // Replace value with expiration
            Assert.True(await cache.ReplaceAsync(cacheKey, "with-expiration", TimeSpan.FromHours(1)));
            result = await cache.GetAsync<string>(cacheKey);
            Assert.NotNull(result);
            Assert.Equal("with-expiration", result.Value);
            var expiration = await cache.GetExpirationAsync(cacheKey);
            Assert.NotNull(expiration);
            Assert.True(expiration.Value > TimeSpan.Zero);
        }
    }

    public virtual async Task ReplaceAsync_WithNonExistentKey_ReturnsFalseAndDoesNotCreateKey()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            const string cacheKey = "non-existent";
            Assert.False(await cache.ReplaceAsync(cacheKey, "value"));
            var result = await cache.GetAsync<string>(cacheKey);
            Assert.False(result.HasValue);

            // Verify case-sensitivity: set keys with different cases, replace only exact matches
            await cache.SetAsync("TEST", 1);
            await cache.SetAsync("test", 2);

            Assert.True(await cache.ReplaceAsync("TEST", 10));
            Assert.Equal(10, (await cache.GetAsync<int>("TEST")).Value);
            Assert.Equal(2, (await cache.GetAsync<int>("test")).Value);

            Assert.True(await cache.ReplaceAsync("test", 20));
            Assert.Equal(10, (await cache.GetAsync<int>("TEST")).Value);
            Assert.Equal(20, (await cache.GetAsync<int>("test")).Value);
        }
    }

    public virtual async Task ReplaceAsync_WithInvalidKey_ThrowsException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () => await cache.ReplaceAsync(null!, 1));
            await Assert.ThrowsAsync<ArgumentException>(async () => await cache.ReplaceAsync(String.Empty, 1));
        }
    }
}
