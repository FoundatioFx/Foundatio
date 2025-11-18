using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Foundatio.Caching;
using Xunit;

namespace Foundatio.Tests.Caching;

public abstract partial class CacheClientTestsBase
{
    public virtual async Task ListAddAsync_WithDuplicates_RemovesDuplicatesAndAddsItems()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            Assert.Equal(3, await cache.ListAddAsync("set", new List<int> { 1, 1, 2, 3 }));
            var result = await cache.GetListAsync<int>("set");
            Assert.NotNull(result);
            Assert.Equal(3, result.Value.Count);

            Assert.True(await cache.ListRemoveAsync("set", 1));
            result = await cache.GetListAsync<int>("set");
            Assert.NotNull(result);
            Assert.Equal(2, result.Value.Count);
        }
    }

    public virtual async Task ListAddAsync_WithNullKey_ThrowsArgumentNullException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            await Assert.ThrowsAsync<ArgumentNullException>(() => cache.ListAddAsync(null, 1));
            await Assert.ThrowsAsync<ArgumentNullException>(() => cache.ListAddAsync(String.Empty, 1));
        }
    }

    public virtual async Task ListAddAsync_WithNullValues_ThrowsArgumentNullException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "list";
            await Assert.ThrowsAsync<ArgumentNullException>(() => cache.ListAddAsync(key, null as List<int>));
        }
    }

    public virtual async Task ListAddAsync_WithDuplicates_StoresUniqueValuesOnly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "list";

            await cache.ListAddAsync(key, [1, 2, 3, 3]);
            var result = await cache.GetListAsync<int>(key);
            Assert.NotNull(result);
            Assert.Equal(3, result.Value.Count);
        }
    }

    public virtual async Task ListAddAsync_WithEmptyCollection_NoOp()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "list";

            await cache.ListAddAsync<int>(key, []);

            await cache.ListAddAsync(key, 1);
            await cache.ListAddAsync(key, 2);
            await cache.ListAddAsync(key, 3);
            var result = await cache.GetListAsync<int>(key);
            Assert.NotNull(result);
            Assert.Equal(3, result.Value.Count);
        }
    }

    public virtual async Task ListAddAsync_WithExistingNonListKey_ThrowsException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                await cache.AddAsync("key1", 1);
                await cache.ListAddAsync("key1", 1);
            });
        }
    }

    public virtual async Task ListAddAsync_WithNullCollection_ThrowsArgumentNullException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "list:null-values";

            await Assert.ThrowsAsync<ArgumentNullException>(() => cache.ListAddAsync(key, null as List<string>));
        }
    }

    public virtual async Task ListAddAsync_WithNullItem_IgnoresNull()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "list:null-values";

            Assert.Equal(0, await cache.ListAddAsync<string>(key, [null]));
            Assert.Equal(1, await cache.ListAddAsync(key, ["1", null]));
            var result = await cache.GetListAsync<string>(key);
            Assert.NotNull(result);
            Assert.Single(result.Value);
        }
    }

    /// <summary>
    /// single strings don't get handled as char arrays
    /// </summary>
    public virtual async Task ListAddAsync_WithSingleString_StoresAsStringNotCharArray()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "list:strings";

            await cache.ListAddAsync(key, "my-value");
            var stringResult = await cache.GetListAsync<string>(key);
            Assert.Single(stringResult.Value);
            Assert.Equal("my-value", stringResult.Value.First());

            await cache.ListRemoveAsync(key, "my-value");
            stringResult = await cache.GetListAsync<string>(key);
            Assert.Empty(stringResult.Value);
        }
    }

    public virtual async Task ListAddAsync_WithPastExpiration_RemovesItem()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "list:expiration:add:past";

            Assert.Equal(1, await cache.ListAddAsync(key, [1]));

            Assert.Equal(0, await cache.ListAddAsync(key, [1], TimeSpan.FromSeconds(-1)));
            Assert.False(await cache.ExistsAsync(key));
        }
    }

    public virtual async Task ListAddAsync_WithFutureExpiration_AddsAndExpiresCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "list:expiration:add:future";

            Assert.Equal(1, await cache.ListAddAsync(key, [2], TimeSpan.FromMilliseconds(100)));

            var cacheValue = await cache.GetListAsync<int>(key);
            Assert.True(cacheValue.HasValue);
            Assert.Single(cacheValue.Value);
            Assert.True(cacheValue.Value.Contains(2));

            await Task.Delay(150);
            Assert.False(await cache.ExistsAsync(key));
        }
    }

    public virtual async Task ListAddAsync_WithMultipleExpirations_ExpiresIndividualItems()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "list:expiration:add:multiple";

            Assert.Equal(1, await cache.ListAddAsync(key, [2], TimeSpan.FromMilliseconds(100)));
            Assert.Equal(1, await cache.ListAddAsync(key, [3], TimeSpan.FromMilliseconds(175)));

            var cacheValue = await cache.GetListAsync<int>(key);
            Assert.True(cacheValue.HasValue);
            Assert.Equal(2, cacheValue.Value.Count);
            Assert.True(cacheValue.Value.Contains(2));
            Assert.True(cacheValue.Value.Contains(3));

            await Task.Delay(125);
            cacheValue = await cache.GetListAsync<int>(key);
            Assert.True(cacheValue.HasValue);
            Assert.Single(cacheValue.Value);
            Assert.True(cacheValue.Value.Contains(3));

            await Task.Delay(100);
            Assert.False(await cache.ExistsAsync(key));
        }
    }

    public virtual async Task ListAddAsync_WithEmptyKey_ThrowsArgumentNullException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () => await cache.ListAddAsync(String.Empty, "value"));
        }
    }

    public virtual async Task ListAddAsync_WithWhitespaceKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () => await cache.ListAddAsync("   ", "value"));
        }
    }

    public virtual async Task ListAddAsync_WithDifferentCasedKeys_MaintainsDistinctLists()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.ListAddAsync("queueName", "item1");
            await cache.ListAddAsync("QueueName", "item2");
            await cache.ListAddAsync("QUEUENAME", "item3");

            var lowerList = await cache.GetListAsync<string>("queueName");
            var titleList = await cache.GetListAsync<string>("QueueName");
            var upperList = await cache.GetListAsync<string>("QUEUENAME");

            Assert.Single(lowerList.Value);
            Assert.Contains("item1", lowerList.Value);

            Assert.Single(titleList.Value);
            Assert.Contains("item2", titleList.Value);

            Assert.Single(upperList.Value);
            Assert.Contains("item3", upperList.Value);
        }
    }
}
