using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Foundatio.Caching;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Foundatio.Tests.Caching;

public abstract partial class CacheClientTestsBase
{
    public virtual async Task RemoveAllAsync_WithScopedCache_AffectsOnlyScopedKeys()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var scopedCache1 = new ScopedCacheClient(cache, "scoped1");
            var scopedCache2 = new ScopedCacheClient(cache, "scoped2");

            await cache.SetAsync("test", 1);
            await scopedCache1.SetAsync("test", 2);
            await scopedCache2.SetAsync("test", 3);

            await scopedCache2.RemoveAllAsync();
            Assert.Equal(2, (await scopedCache1.GetAsync<int>("test")).Value);
            Assert.False((await scopedCache2.GetAsync<int>("test")).HasValue);
            Assert.Equal(1, (await cache.GetAsync<int>("test")).Value);
        }
    }

    public virtual async Task RemoveAllAsync_WithLargeNumberOfKeys_RemovesAllKeysEfficiently()
    {
        const int COUNT = 10000;

        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            Assert.Equal(0, await cache.RemoveAllAsync());

            var dictionary = Enumerable.Range(0, COUNT).ToDictionary(i => $"remove-all:{i}");

            var sw = Stopwatch.StartNew();
            await cache.SetAllAsync(dictionary);
            sw.Stop();
            _logger.LogInformation("Set All Time: {Elapsed:g}", sw.Elapsed);

            sw = Stopwatch.StartNew();
            Assert.Equal(COUNT, await cache.RemoveAllAsync());
            sw.Stop();
            _logger.LogInformation("Remove All Time: {Elapsed:g}", sw.Elapsed);

            Assert.False(await cache.ExistsAsync("remove-all:0"));
            Assert.False(await cache.ExistsAsync($"remove-all:{COUNT - 1}"));
        }
    }

    public virtual async Task RemoveAllAsync_WithSpecificKeyCollection_RemovesOnlySpecifiedKeys()
    {
        const int COUNT = 10000;

        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var dictionary = Enumerable.Range(0, COUNT).ToDictionary(i => $"remove-all-keys:{i}");

            var sw = Stopwatch.StartNew();
            await cache.SetAllAsync(dictionary);
            sw.Stop();
            _logger.LogInformation("Set All Time: {Elapsed:g}", sw.Elapsed);

            sw = Stopwatch.StartNew();
            Assert.Equal(COUNT, await cache.RemoveAllAsync(dictionary.Keys));
            sw.Stop();
            _logger.LogInformation("Remove All Time: {Elapsed:g}", sw.Elapsed);

            Assert.False(await cache.ExistsAsync("remove-all-keys:0"));
            Assert.False(await cache.ExistsAsync($"remove-all-keys:{COUNT - 1}"));

            // Verify case sensitivity - only exact matches are removed
            await cache.SetAsync("cacheKey", "val1");
            await cache.SetAsync("CacheKey", "val2");
            await cache.SetAsync("CACHEKEY", "val3");

            await cache.RemoveAllAsync(["CacheKey"]);

            Assert.True((await cache.GetAsync<string>("cacheKey")).HasValue);
            Assert.False((await cache.GetAsync<string>("CacheKey")).HasValue);
            Assert.True((await cache.GetAsync<string>("CACHEKEY")).HasValue);
        }
    }

    public virtual async Task RemoveAllAsync_WithInvalidKeys_ValidatesCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            // Keys containing null throws ArgumentNullException
            await Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await cache.RemoveAllAsync(["key1", null, "key2"]));

            // Keys containing empty string throws ArgumentException
            await Assert.ThrowsAsync<ArgumentException>(async () =>
                await cache.RemoveAllAsync(["key1", String.Empty, "key2"]));

            // Empty keys collection succeeds (no-op)
            await cache.RemoveAllAsync([]);

            // Null keys removes all values
            await cache.SetAsync("key1", 1);
            await cache.SetAsync("key2", 2);
            Assert.True(await cache.ExistsAsync("key1"));
            Assert.True(await cache.ExistsAsync("key2"));

            Assert.Equal(2, await cache.RemoveAllAsync(null));
            Assert.False(await cache.ExistsAsync("key1"));
            Assert.False(await cache.ExistsAsync("key2"));
        }
    }
}
