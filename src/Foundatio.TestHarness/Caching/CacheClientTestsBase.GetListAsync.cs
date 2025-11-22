using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Foundatio.Utility;
using Xunit;

namespace Foundatio.Tests.Caching;

public abstract partial class CacheClientTestsBase
{
    public virtual async Task GetListAsync_WithNullKey_ThrowsArgumentNullException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            await Assert.ThrowsAsync<ArgumentNullException>(() => cache.GetListAsync<ICollection<int>>(null));
        }
    }

    public virtual async Task GetListAsync_WithPaging_ReturnsCorrectPageSize(string cacheKey)
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            int[] values = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20];
            await cache.ListAddAsync(cacheKey, values, TimeSpan.FromMinutes(1));

            var pagedResult = await cache.GetListAsync<int>(cacheKey, 1, 5);
            Assert.NotNull(pagedResult);
            Assert.Equal(5, pagedResult.Value.Count);
        }
    }

    public virtual async Task GetListAsync_WithMultiplePages_ReturnsAllItems()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "list:paging:multiple";

            int[] values = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20];
            await cache.ListAddAsync(key, values, TimeSpan.FromMinutes(1));

            var actualResults = new HashSet<int>(values.Length);

            for (int page = 1; page < values.Length / 5 + 1; page++)
            {
                var pagedResult = await cache.GetListAsync<int>(key, page, 5);
                Assert.NotNull(pagedResult);
                Assert.Equal(5, pagedResult.Value.Count);
                actualResults.AddRange(pagedResult.Value);
            }

            Assert.Equal(values.Length, actualResults.Count);
        }
    }

    public virtual async Task GetListAsync_WithNewItemsAdded_ReturnsNewItemsLast()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "list:paging:newitems";

            int[] values = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20];
            await cache.ListAddAsync(key, values, TimeSpan.FromMinutes(1));

            var firstPageResults = new HashSet<int>(5);
            var firstResult = await cache.GetListAsync<int>(key, 1, 5);
            firstPageResults.AddRange(firstResult.Value);

            await cache.ListAddAsync(key, [21, 22], TimeSpan.FromMinutes(2));
            var lastPageResult = await cache.GetListAsync<int>(key, 5, 5);
            Assert.NotNull(lastPageResult);
            Assert.Equal(2, lastPageResult.Value.Count);

            var firstPageAgain = await cache.GetListAsync<int>(key, 1, 5);
            Assert.Equal(firstPageResults, firstPageAgain.Value.ToArray());
        }
    }

    public virtual async Task GetListAsync_WithInvalidPageNumber_ThrowsArgumentOutOfRangeException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "list:paging:invalid";

            int[] values = [1, 2, 3, 4, 5];
            await cache.ListAddAsync(key, values, TimeSpan.FromMinutes(1));

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => cache.GetListAsync<int>(key, 0, 5));
        }
    }

    public virtual async Task GetListAsync_WithPageBeyondEnd_ReturnsEmptyCollection()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "list:paging:beyond";

            int[] values = [1, 2, 3, 4, 5];
            await cache.ListAddAsync(key, values, TimeSpan.FromMinutes(1));

            var pagedResult = await cache.GetListAsync<int>(key, 10, 5);
            Assert.NotNull(pagedResult);
            Assert.Empty(pagedResult.Value);
        }
    }

    public virtual async Task GetListAsync_WithExpiredItems_RemovesExpiredAndReturnsActive()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "list:expiration:get";

            Assert.Equal(1, await cache.ListAddAsync(key, [1], TimeSpan.FromMilliseconds(100)));

            var cacheValue = await cache.GetListAsync<int>(key);
            Assert.True(cacheValue.HasValue);
            Assert.Single(cacheValue.Value);
            Assert.True(cacheValue.Value.Contains(1));

            await Task.Delay(150);

            // GetList should invalidate expired items
            cacheValue = await cache.GetListAsync<int>(key);
            Assert.False(cacheValue.HasValue);
            Assert.False(await cache.ExistsAsync(key));
        }
    }

    public virtual async Task GetListAsync_WithEmptyKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentException>(async () => await cache.GetListAsync<string>(String.Empty));
        }
    }

}
