using System;
using System.Threading.Tasks;
using Xunit;

namespace Foundatio.Tests.Caching;

public abstract partial class CacheClientTestsBase
{
    public virtual async Task RemoveIfEqualAsync_WithInvalidKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await cache.RemoveIfEqualAsync(null!, "value"));

            await Assert.ThrowsAsync<ArgumentException>(async () =>
                await cache.RemoveIfEqualAsync(String.Empty, "value"));
        }
    }

    public virtual async Task RemoveIfEqualAsync_WithMatchingValue_ReturnsTrueAndRemoves()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            Assert.True(await cache.AddAsync("session:active", "123"));

            Assert.True(await cache.RemoveIfEqualAsync("session:active", "123"));
            var result = await cache.GetAsync<string>("session:active");
            Assert.NotNull(result);
            Assert.False(result.HasValue);
        }
    }

    public virtual async Task RemoveIfEqualAsync_WithMismatchedValue_ReturnsFalseAndDoesNotRemove()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            Assert.True(await cache.AddAsync("remove-if-equal", "123"));
            var result = await cache.GetAsync<string>("remove-if-equal");
            Assert.NotNull(result);
            Assert.Equal("123", result.Value);

            Assert.False(await cache.RemoveIfEqualAsync("remove-if-equal", "789"));
            result = await cache.GetAsync<string>("remove-if-equal");
            Assert.NotNull(result);
            Assert.Equal("123", result.Value);
        }
    }
}
