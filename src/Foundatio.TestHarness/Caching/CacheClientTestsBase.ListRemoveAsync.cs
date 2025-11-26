using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Foundatio.Caching;
using Xunit;

namespace Foundatio.Tests.Caching;

public abstract partial class CacheClientTestsBase
{
    public virtual async Task ListRemoveAsync_WithInvalidInputs_ThrowsAppropriateException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            // Null key throws ArgumentNullException
            await Assert.ThrowsAsync<ArgumentNullException>(() => cache.ListRemoveAsync(null, 1));

            // Empty key throws ArgumentException
            await Assert.ThrowsAsync<ArgumentException>(() => cache.ListRemoveAsync(String.Empty, "value"));

            // Null collection throws ArgumentNullException
            await Assert.ThrowsAsync<ArgumentNullException>(() => cache.ListRemoveAsync("list:remove:test", null as List<int>));
        }
    }

    public virtual async Task ListRemoveAsync_WithValues_RemovesCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "list:remove:values";

            // Remove multiple values at once
            await cache.ListAddAsync(key, [1, 2, 3, 3]);
            await cache.ListRemoveAsync(key, [1, 3]);
            var result = await cache.GetListAsync<int>(key);
            Assert.NotNull(result);
            Assert.Single(result.Value);
            Assert.Contains(2, result.Value);

            // Remove remaining value
            await cache.ListRemoveAsync(key, [2]);
            result = await cache.GetListAsync<int>(key);
            Assert.NotNull(result);
            Assert.Empty(result.Value);

            // Null items in collection are ignored - use different key to avoid type conflict
            const string nullItemsKey = "list:remove:nullitems";
            await cache.ListAddAsync(nullItemsKey, ["1"]);
            Assert.Equal(0, await cache.ListRemoveAsync<string>(nullItemsKey, [null]));
            Assert.Equal(1, await cache.ListRemoveAsync(nullItemsKey, ["1", null]));
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
            const string key = "list:remove:cleanup";

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
}
