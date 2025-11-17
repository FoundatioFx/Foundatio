using System;
using System.Threading.Tasks;
using Xunit;

namespace Foundatio.Tests.Caching;

public abstract partial class CacheClientTestsBase
{
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

            const string cacheKey = "replace-if-equal";
            Assert.True(await cache.AddAsync(cacheKey, "123"));

            Assert.True(await cache.ReplaceIfEqualAsync(cacheKey, "456", "123"));
            var result = await cache.GetAsync<string>(cacheKey);
            Assert.NotNull(result);
            Assert.Equal("456", result.Value);
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

    public virtual async Task ReplaceIfEqualAsync_WithNullKey_ThrowsArgumentNullException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await cache.ReplaceIfEqualAsync<string>(null, "old", "new"));
        }
    }

    public virtual async Task ReplaceIfEqualAsync_WithEmptyKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentException>(async () =>
                await cache.ReplaceIfEqualAsync(String.Empty, "old", "new"));
        }
    }

    public virtual async Task ReplaceIfEqualAsync_WithWhitespaceKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentException>(async () =>
                await cache.ReplaceIfEqualAsync("   ", "old", "new"));
        }
    }

    public virtual async Task ReplaceIfEqualAsync_WithDifferentCasedKeys_ReplacesOnlyExactMatch()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.SetAsync("statusCode", 200);
            await cache.SetAsync("StatusCode", 201);
            await cache.SetAsync("STATUSCODE", 202);

            var replaced = await cache.ReplaceIfEqualAsync("StatusCode", 201, 299);

            Assert.True(replaced);

            var lower = await cache.GetAsync<int>("statusCode");
            var title = await cache.GetAsync<int>("StatusCode");
            var upper = await cache.GetAsync<int>("STATUSCODE");

            Assert.Equal(200, lower.Value);
            Assert.Equal(299, title.Value);
            Assert.Equal(202, upper.Value);
        }
    }
}
