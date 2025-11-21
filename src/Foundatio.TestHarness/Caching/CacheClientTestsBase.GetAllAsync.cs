using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Foundatio.Caching;
using Xunit;

namespace Foundatio.Tests.Caching;

public abstract partial class CacheClientTestsBase
{
    public virtual async Task GetAllAsync_WithExistingKeys_ReturnsAllValues(string cacheKey)
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            await cache.SetAsync("test1", 1);
            await cache.SetAsync(cacheKey, 2);
            await cache.SetAsync("test3", 3);
            var result = await cache.GetAllAsync<int>(["test1", cacheKey, "test3"]);
            Assert.NotNull(result);
            Assert.Equal(3, result.Count);
            Assert.Equal(1, result["test1"].Value);
            Assert.Equal(2, result[cacheKey].Value);
            Assert.Equal(3, result["test3"].Value);
        }
    }

    public virtual async Task GetAllAsync_WithMixedObjectTypes_ReturnsCorrectValues()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            await cache.SetAsync("obj1", new SimpleModel { Data1 = "data 1", Data2 = 1 });
            await cache.SetAsync("obj2", new SimpleModel { Data1 = "data 2", Data2 = 2 });
            await cache.SetAsync("obj4", new SimpleModel { Data1 = "test 1", Data2 = 4 });

            var result = await cache.GetAllAsync<SimpleModel>(["obj1", "obj2", "obj4"]);
            Assert.NotNull(result);
            Assert.Equal(3, result.Count);

            var obj4 = result["obj4"];
            Assert.NotNull(obj4);
            Assert.Equal("test 1", obj4.Value.Data1);
        }
    }

    public virtual async Task GetAllAsync_WithNullValues_HandlesNullsCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            await cache.SetAsync("obj3", (SimpleModel)null);
            var result = await cache.GetAllAsync<SimpleModel>(["obj3"]);
            Assert.NotNull(result);
            Assert.Single(result);
            Assert.True(result["obj3"].IsNull);

            await cache.SetAsync("str1", "string 1");
            await cache.SetAsync("str3", (string)null);
            var result2 = await cache.GetAllAsync<string>(["str1", "str3"]);
            Assert.NotNull(result2);
            Assert.Equal(2, result2.Count);
        }
    }

    public virtual async Task GetAllAsync_WithNonExistentKeys_ReturnsEmptyResults()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            await cache.SetAsync("obj1", new SimpleModel { Data1 = "data 1", Data2 = 1 });
            var result = await cache.GetAllAsync<SimpleModel>(["obj1", "obj5"]);
            Assert.NotNull(result);
            Assert.Equal(2, result.Count);
            Assert.False(result["obj5"].HasValue);
        }
    }

    public virtual async Task GetAllAsync_WithOverlappingKeys_UsesLatestValues()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            await cache.SetAsync("test1", 1.0);
            await cache.SetAsync("test2", 2.0);
            await cache.SetAsync("test3", 3.0);
            await cache.SetAllAsync(new Dictionary<string, double>
            {
                { "test3", 3.5 }, { "test4", 4.0 }, { "test5", 5.0 }
            });

            var result = await cache.GetAllAsync<double>(["test1", "test2", "test3", "test4", "test5"]);
            Assert.NotNull(result);
            Assert.Equal(5, result.Count);
            Assert.Equal(1.0, result["test1"].Value);
            Assert.Equal(2.0, result["test2"].Value);
            Assert.Equal(3.5, result["test3"].Value);
            Assert.Equal(4.0, result["test4"].Value);
            Assert.Equal(5.0, result["test5"].Value);
        }
    }

    public virtual async Task GetAllAsync_WithScopedCache_ReturnsUnscopedKeys()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var scopedCache1 = new ScopedCacheClient(cache, "scoped1");
            var nestedScopedCache1 = new ScopedCacheClient(scopedCache1, "nested");

            await scopedCache1.SetAsync("test", 1);
            await nestedScopedCache1.SetAsync("test", 2);

            Assert.Equal("test", (await scopedCache1.GetAllAsync<int>("test")).Keys.FirstOrDefault());
            Assert.Equal("test", (await nestedScopedCache1.GetAllAsync<int>("test")).Keys.FirstOrDefault());
        }
    }

    public virtual async Task GetAllAsync_WithNullKeys_ThrowsArgumentNullException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () => await cache.GetAllAsync<string>(null));
        }
    }

    public virtual async Task GetAllAsync_WithEmptyKeys_ReturnsEmpty()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            var result = await cache.GetAllAsync<string>([]);
            Assert.NotNull(result);
            Assert.Empty(result);
        }
    }

    public virtual async Task GetAllAsync_WithKeysContainingNull_ThrowsArgumentNullException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await cache.GetAllAsync<string>(["key1", null, "key2"]));
        }
    }

    public virtual async Task GetAllAsync_WithKeysContainingEmpty_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentException>(async () =>
                await cache.GetAllAsync<string>(["key1", String.Empty, "key2"]));
        }
    }

    public virtual async Task GetAllAsync_WithMixedCaseKeys_RetrievesExactMatches()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.SetAsync("configKey", "value1");
            await cache.SetAsync("ConfigKey", "value2");
            await cache.SetAsync("CONFIGKEY", "value3");

            var results = await cache.GetAllAsync<string>(["configKey", "ConfigKey", "CONFIGKEY"]);

            Assert.Equal(3, results.Count);
            Assert.Equal("value1", results["configKey"].Value);
            Assert.Equal("value2", results["ConfigKey"].Value);
            Assert.Equal("value3", results["CONFIGKEY"].Value);
        }
    }
}
