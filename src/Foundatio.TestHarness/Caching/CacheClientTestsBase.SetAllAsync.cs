using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Foundatio.Caching;
using Xunit;

namespace Foundatio.Tests.Caching;

public abstract partial class CacheClientTestsBase
{
    public virtual async Task SetAllAsync_WithDateTimeMinValue_DoesNotAddKeys()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            // Ensure keys are not added when they are already expired
            Assert.Equal(0,
                await cache.SetAllAsync(
                    new Dictionary<string, object> { { "test1", 1 }, { "test2", 2 }, { "test3", 3 } },
                    DateTime.MinValue));

            Assert.False(await cache.ExistsAsync("test1"));
            Assert.False(await cache.ExistsAsync("test2"));
            Assert.False(await cache.ExistsAsync("test3"));
        }
    }

    public virtual async Task SetAllAsync_WithExpiration_KeysExpireCorrectly(string cacheKey)
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            var expiry = TimeSpan.FromMilliseconds(50);
            await cache.SetAllAsync(new Dictionary<string, object> { { cacheKey, "value" } }, expiry);

            // Add 10ms to the expiry to ensure the cache has expired as the delay window is not guaranteed to be exact.
            await Task.Delay(expiry.Add(TimeSpan.FromMilliseconds(10)));

            Assert.False(await cache.ExistsAsync(cacheKey));
        }
    }

    public virtual async Task SetAllAsync_WithNullItems_ThrowsArgumentNullException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () => await cache.SetAllAsync<string>(null));
        }
    }

    public virtual async Task SetAllAsync_WithEmptyItems_ReturnsTrue()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            int result = await cache.SetAllAsync(new Dictionary<string, string>());
            Assert.Equal(0, result);
        }
    }

    public virtual async Task SetAllAsync_WithItemsContainingEmptyKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            var items = new Dictionary<string, string> { { "key1", "value1" }, { String.Empty, "value2" } };

            await Assert.ThrowsAsync<ArgumentException>(async () => await cache.SetAllAsync(items));
        }
    }

    public virtual async Task SetAllAsync_WithDifferentCasedKeys_CreatesDistinctEntries()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            var items = new Dictionary<string, int> { { "itemId", 1 }, { "ItemId", 2 }, { "ITEMID", 3 } };

            await cache.SetAllAsync(items);

            var results = await cache.GetAllAsync<int>(["itemId", "ItemId", "ITEMID"]);

            Assert.Equal(3, results.Count);
            Assert.Equal(1, results["itemId"].Value);
            Assert.Equal(2, results["ItemId"].Value);
            Assert.Equal(3, results["ITEMID"].Value);
        }
    }
}
