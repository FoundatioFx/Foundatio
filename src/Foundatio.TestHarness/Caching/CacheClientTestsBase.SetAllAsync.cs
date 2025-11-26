using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Foundatio.Caching;
using Xunit;

namespace Foundatio.Tests.Caching;

public abstract partial class CacheClientTestsBase
{
    public virtual async Task SetAllAsync_WithExpiration_KeysExpireCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            // DateTime.MinValue should not add keys (already expired)
            Assert.Equal(0,
                await cache.SetAllAsync(
                    new Dictionary<string, object> { { "expired1", 1 }, { "expired2", 2 } },
                    DateTime.MinValue));
            Assert.False(await cache.ExistsAsync("expired1"));
            Assert.False(await cache.ExistsAsync("expired2"));

            // Use mixed-case keys to also verify case-sensitivity
            var expiry = TimeSpan.FromMilliseconds(50);
            var items = new Dictionary<string, int> { { "itemId", 1 }, { "ItemId", 2 }, { "ITEMID", 3 } };
            await cache.SetAllAsync(items, expiry);

            // Verify case-sensitivity: all three distinct keys should exist
            var results = await cache.GetAllAsync<int>(["itemId", "ItemId", "ITEMID"]);
            Assert.Equal(3, results.Count);
            Assert.Equal(1, results["itemId"].Value);
            Assert.Equal(2, results["ItemId"].Value);
            Assert.Equal(3, results["ITEMID"].Value);

            // Add 10ms to the expiry to ensure the cache has expired as the delay window is not guaranteed to be exact.
            await Task.Delay(expiry.Add(TimeSpan.FromMilliseconds(10)));

            Assert.False(await cache.ExistsAsync("itemId"));
            Assert.False(await cache.ExistsAsync("ItemId"));
            Assert.False(await cache.ExistsAsync("ITEMID"));
        }
    }

    public virtual async Task SetAllAsync_WithInvalidItems_ValidatesCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            // Null items throws ArgumentNullException
            await Assert.ThrowsAsync<ArgumentNullException>(async () => await cache.SetAllAsync<string>(null));

            // Items containing empty key throws ArgumentException
            var itemsWithEmptyKey = new Dictionary<string, string> { { "key1", "value1" }, { String.Empty, "value2" } };
            await Assert.ThrowsAsync<ArgumentException>(async () => await cache.SetAllAsync(itemsWithEmptyKey));

            // Empty items collection returns 0 (not an error)
            int result = await cache.SetAllAsync(new Dictionary<string, string>());
            Assert.Equal(0, result);
        }
    }

}
