using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Foundatio.Caching;
using Xunit;

namespace Foundatio.Tests.Caching;

public abstract partial class CacheClientTestsBase
{
    public virtual async Task ListRemoveAsync_WithNullKey_ThrowsArgumentNullException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            await Assert.ThrowsAsync<ArgumentNullException>(() => cache.ListRemoveAsync(null, 1));
            await Assert.ThrowsAsync<ArgumentNullException>(() => cache.ListRemoveAsync(String.Empty, 1));
        }
    }

    public virtual async Task ListRemoveAsync_WithNullValues_ThrowsArgumentNullException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "list";
            await Assert.ThrowsAsync<ArgumentNullException>(() => cache.ListRemoveAsync(key, null as List<int>));
        }
    }

    public virtual async Task ListRemoveAsync_WithMultipleValues_RemovesAll()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "list";

            await cache.ListAddAsync(key, [1, 2, 3, 3]);
            await cache.ListRemoveAsync(key, [1, 2, 3]);
            var result = await cache.GetListAsync<int>(key);
            Assert.NotNull(result);
            Assert.Empty(result.Value);
        }
    }

    public virtual async Task ListRemoveAsync_WithSingleValue_RemovesCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "list";

            await cache.ListAddAsync(key, 1);
            await cache.ListAddAsync(key, 2);
            await cache.ListAddAsync(key, 3);

            await cache.ListRemoveAsync(key, 2);
            var result = await cache.GetListAsync<int>(key);
            Assert.NotNull(result);
            Assert.Equal(2, result.Value.Count);

            await cache.ListRemoveAsync(key, 1);
            await cache.ListRemoveAsync(key, 3);
            result = await cache.GetListAsync<int>(key);
            Assert.NotNull(result);
            Assert.Empty(result.Value);
        }
    }

    public virtual async Task ListRemoveAsync_WithNullCollection_ThrowsArgumentNullException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "list:null-values";

            await Assert.ThrowsAsync<ArgumentNullException>(() => cache.ListRemoveAsync(key, null as List<string>));
        }
    }

    public virtual async Task ListRemoveAsync_WithNullItem_IgnoresNull()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "list:null-values";

            await cache.ListAddAsync(key, ["1"]);
            Assert.Equal(0, await cache.ListRemoveAsync<string>(key, [null]));
            Assert.Equal(1, await cache.ListRemoveAsync(key, ["1", null]));
            var result = await cache.GetListAsync<string>(key);
            Assert.NotNull(result);
            Assert.Empty(result.Value);
        }
    }

    public virtual async Task ListRemoveAsync_WithValidValues_RemovesKeyWhenEmpty()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "list:expiration:remove:past";

            Assert.Equal(2, await cache.ListAddAsync(key, [1, 2]));

            Assert.Equal(1, await cache.ListRemoveAsync(key, [1], TimeSpan.FromSeconds(-1)));
            Assert.Equal(0, await cache.ListRemoveAsync(key, [1], TimeSpan.FromSeconds(-1)));

            var cacheValue = await cache.GetListAsync<int>(key);
            Assert.True(cacheValue.HasValue);
            Assert.Single(cacheValue.Value);
            Assert.True(cacheValue.Value.Contains(2));

            // Expiration is not taken into account since it's a remove operation.
            Assert.Equal(1, await cache.ListRemoveAsync(key, [2], TimeSpan.FromSeconds(1)));
            Assert.False(await cache.ExistsAsync(key));
        }
    }

    public virtual async Task ListRemoveAsync_WithEmptyKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentException>(async () => await cache.ListRemoveAsync(String.Empty, "value"));
        }
    }

    public virtual async Task ListRemoveAsync_WithWhitespaceKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentException>(async () => await cache.ListRemoveAsync("   ", "value"));
        }
    }
}
