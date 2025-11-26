using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Foundatio.Utility;
using Xunit;

namespace Foundatio.Tests.Caching;

public abstract partial class CacheClientTestsBase
{
    public virtual async Task GetListAsync_WithInvalidKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "list:validation";

            await Assert.ThrowsAsync<ArgumentNullException>(() => cache.GetListAsync<int>(null!));
            await Assert.ThrowsAsync<ArgumentException>(() => cache.GetListAsync<int>(String.Empty));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => cache.GetListAsync<int>(key, 0, 5));
        }
    }

    public virtual async Task GetListAsync_WithPaging_ReturnsCorrectResults()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "list:paging";

            int[] values = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20];
            await cache.ListAddAsync(key, values, TimeSpan.FromMinutes(1));

            // Verify first page returns correct page size
            var firstPage = await cache.GetListAsync<int>(key, 1, 5);
            Assert.NotNull(firstPage);
            Assert.Equal(5, firstPage.Value.Count);
            var firstPageItems = new HashSet<int>(firstPage.Value);

            // Verify all items can be retrieved across multiple pages
            var allItems = new HashSet<int>(values.Length);
            for (int page = 1; page <= values.Length / 5; page++)
            {
                var pagedResult = await cache.GetListAsync<int>(key, page, 5);
                Assert.NotNull(pagedResult);
                Assert.Equal(5, pagedResult.Value.Count);
                allItems.AddRange(pagedResult.Value);
            }
            Assert.Equal(values.Length, allItems.Count);

            // Verify page beyond end returns empty collection
            var beyondEnd = await cache.GetListAsync<int>(key, 10, 5);
            Assert.NotNull(beyondEnd);
            Assert.Empty(beyondEnd.Value);

            // Verify new items are added at the end and first page remains stable
            await cache.ListAddAsync(key, [21, 22], TimeSpan.FromMinutes(1));
            var lastPageResult = await cache.GetListAsync<int>(key, 5, 5);
            Assert.NotNull(lastPageResult);
            Assert.Equal(2, lastPageResult.Value.Count);

            var firstPageAgain = await cache.GetListAsync<int>(key, 1, 5);
            Assert.Equal(firstPageItems, firstPageAgain.Value.ToHashSet());
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
            const string key = "list:expiration";

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
}
