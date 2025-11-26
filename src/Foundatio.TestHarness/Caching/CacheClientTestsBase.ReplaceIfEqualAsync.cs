using System;
using System.Threading.Tasks;
using Xunit;

namespace Foundatio.Tests.Caching;

public abstract partial class CacheClientTestsBase
{
    public virtual async Task ReplaceIfEqualAsync_WithInvalidKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await cache.ReplaceIfEqualAsync(null!, "old", "new"));
            await Assert.ThrowsAsync<ArgumentException>(async () =>
                await cache.ReplaceIfEqualAsync(String.Empty, "old", "new"));
        }
    }

    public virtual async Task ReplaceIfEqualAsync_WithMatchingOldValue_ReturnsTrueAndReplacesValue()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            const string cacheKey = "workflow:state";
            Assert.True(await cache.AddAsync(cacheKey, "123"));

            Assert.True(await cache.ReplaceIfEqualAsync(cacheKey, "456", "123"));
            var result = await cache.GetAsync<string>(cacheKey);
            Assert.NotNull(result);
            Assert.Equal("456", result.Value);
        }
    }

    public virtual async Task ReplaceIfEqualAsync_WithMismatchedOldValue_ReturnsFalseAndDoesNotReplace()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            const string cacheKey = "replace-if-equal";
            Assert.True(await cache.AddAsync(cacheKey, "123"));
            var result = await cache.GetAsync<string>(cacheKey);
            Assert.NotNull(result);
            Assert.Equal("123", result.Value);

            Assert.False(await cache.ReplaceIfEqualAsync(cacheKey, "456", "789"));
            result = await cache.GetAsync<string>(cacheKey);
            Assert.NotNull(result);
            Assert.Equal("123", result.Value);

            // Verify case-sensitivity: different-cased keys are distinct, replace only exact match
            Assert.True(await cache.AddAsync("statusCode", 200));
            Assert.True(await cache.AddAsync("StatusCode", 201));
            Assert.True(await cache.AddAsync("STATUSCODE", 202));

            Assert.True(await cache.ReplaceIfEqualAsync("StatusCode", 299, 201));

            Assert.Equal(200, (await cache.GetAsync<int>("statusCode")).Value);
            Assert.Equal(299, (await cache.GetAsync<int>("StatusCode")).Value);
            Assert.Equal(202, (await cache.GetAsync<int>("STATUSCODE")).Value);
        }
    }

    public virtual async Task ReplaceIfEqualAsync_WithExpiration_SetsExpirationCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            const string cacheKey = "replace-if-equal";
            Assert.True(await cache.AddAsync(cacheKey, "123"));
            Assert.Null(await cache.GetExpirationAsync(cacheKey));

            Assert.True(await cache.ReplaceIfEqualAsync(cacheKey, "456", "123", TimeSpan.FromHours(1)));
            Assert.NotNull(await cache.GetExpirationAsync(cacheKey));
        }
    }
}
