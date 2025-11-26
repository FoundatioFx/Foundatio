using System;
using System.Linq;
using System.Threading.Tasks;
using Foundatio.Caching;
using Xunit;

namespace Foundatio.Tests.Caching;

public abstract partial class CacheClientTestsBase
{
    public virtual async Task GetAllAsync_WithInvalidKeys_ValidatesCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            // Null keys collection throws ArgumentNullException
            await Assert.ThrowsAsync<ArgumentNullException>(async () => await cache.GetAllAsync<string>(null));

            // Keys containing null throws ArgumentNullException
            await Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await cache.GetAllAsync<string>(["key1", null, "key2"]));

            // Keys containing empty string throws ArgumentException
            await Assert.ThrowsAsync<ArgumentException>(async () =>
                await cache.GetAllAsync<string>(["key1", "", "key2"]));

            // Empty keys collection returns empty result (not an error)
            var result = await cache.GetAllAsync<string>([]);
            Assert.NotNull(result);
            Assert.Empty(result);
        }
    }

    public virtual async Task GetAllAsync_WithMultipleKeys_ReturnsCorrectValues()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            // Test with primitive values including mixed-case, whitespace keys, and null
            await cache.SetAsync("test1", 1);
            await cache.SetAsync("Test1", 2);  // Mixed case - different key
            await cache.SetAsync("   ", 3);    // Whitespace key
            var intResult = await cache.GetAllAsync<int>(["test1", "Test1", "   ", "nonexistent"]);
            Assert.NotNull(intResult);
            Assert.Equal(4, intResult.Count);
            Assert.Equal(1, intResult["test1"].Value);
            Assert.Equal(2, intResult["Test1"].Value);
            Assert.Equal(3, intResult["   "].Value);
            Assert.False(intResult["nonexistent"].HasValue);

            // Test with complex objects including null values
            await cache.SetAsync("obj1", new SimpleModel { Data1 = "data 1", Data2 = 1 });
            await cache.SetAsync("Obj1", new SimpleModel { Data1 = "data 2", Data2 = 2 });  // Mixed case
            await cache.SetAsync("objNull", (SimpleModel)null);
            var objResult = await cache.GetAllAsync<SimpleModel>(["obj1", "Obj1", "objNull", "objMissing"]);
            Assert.NotNull(objResult);
            Assert.Equal(4, objResult.Count);
            Assert.Equal("data 1", objResult["obj1"].Value.Data1);
            Assert.Equal("data 2", objResult["Obj1"].Value.Data1);
            Assert.True(objResult["objNull"].IsNull);
            Assert.False(objResult["objMissing"].HasValue);
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
}
