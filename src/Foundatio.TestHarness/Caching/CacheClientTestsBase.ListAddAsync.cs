using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Foundatio.Caching;
using Xunit;

namespace Foundatio.Tests.Caching;

public abstract partial class CacheClientTestsBase
{
    public virtual async Task ListAddAsync_WithExpiration_ExpiresCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "list:expiration";

            // Past expiration removes item (no delay needed)
            Assert.Equal(1, await cache.ListAddAsync(key, [1]));
            Assert.Equal(0, await cache.ListAddAsync(key, [1], TimeSpan.FromSeconds(-1)));
            Assert.False(await cache.ExistsAsync(key));

            // Multiple expirations expire individual items - test staggered expiration in one pass
            Assert.Equal(1, await cache.ListAddAsync(key, [2], TimeSpan.FromMilliseconds(50)));
            Assert.Equal(1, await cache.ListAddAsync(key, [3], TimeSpan.FromMilliseconds(150)));

            var cacheValue = await cache.GetListAsync<int>(key);
            Assert.True(cacheValue.HasValue);
            Assert.Equal(2, cacheValue.Value.Count);

            // Wait for first item to expire
            await Task.Delay(75);
            cacheValue = await cache.GetListAsync<int>(key);
            Assert.True(cacheValue.HasValue);
            Assert.Single(cacheValue.Value);
            Assert.Contains(3, cacheValue.Value);

            // Wait for second item to expire
            await Task.Delay(100);
            Assert.False(await cache.ExistsAsync(key));
        }
    }

    public virtual async Task ListAddAsync_WithInvalidArguments_ThrowsException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            // Null key
            await Assert.ThrowsAsync<ArgumentNullException>(() => cache.ListAddAsync(null, 1));

            // Empty key
            await Assert.ThrowsAsync<ArgumentException>(() => cache.ListAddAsync(String.Empty, "value"));

            // Null collection
            await Assert.ThrowsAsync<ArgumentNullException>(() => cache.ListAddAsync("list:validation", null as List<int>));

            // Existing non-list key
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                await cache.AddAsync("list:non-list-key", 1);
                await cache.ListAddAsync("list:non-list-key", 1);
            });
        }
    }

    public virtual async Task ListAddAsync_WithSingleString_StoresAsStringNotCharArray()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "list:string";

            await cache.ListAddAsync(key, "my-value");
            var stringResult = await cache.GetListAsync<string>(key);
            Assert.Single(stringResult.Value);
            Assert.Equal("my-value", stringResult.Value.First());

            await cache.ListRemoveAsync(key, "my-value");
            stringResult = await cache.GetListAsync<string>(key);
            Assert.Empty(stringResult.Value);
        }
    }

    public virtual async Task ListAddAsync_WithVariousInputs_HandlesCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "list:inputs";

            // Duplicates are stored as unique values only
            Assert.Equal(3, await cache.ListAddAsync(key, new List<int> { 1, 1, 2, 3 }));
            var result = await cache.GetListAsync<int>(key);
            Assert.NotNull(result);
            Assert.Equal(3, result.Value.Count);

            Assert.True(await cache.ListRemoveAsync(key, 1));
            result = await cache.GetListAsync<int>(key);
            Assert.NotNull(result);
            Assert.Equal(2, result.Value.Count);

            await cache.RemoveAllAsync();

            // Empty collection is no-op
            await cache.ListAddAsync<int>(key, []);
            await cache.ListAddAsync(key, 1);
            await cache.ListAddAsync(key, 2);
            await cache.ListAddAsync(key, 3);
            result = await cache.GetListAsync<int>(key);
            Assert.NotNull(result);
            Assert.Equal(3, result.Value.Count);

            await cache.RemoveAllAsync();

            // Null items are ignored
            Assert.Equal(0, await cache.ListAddAsync<string>(key, [null]));
            Assert.Equal(1, await cache.ListAddAsync(key, ["1", null]));
            var stringResult = await cache.GetListAsync<string>(key);
            Assert.NotNull(stringResult);
            Assert.Single(stringResult.Value);
        }
    }
}
