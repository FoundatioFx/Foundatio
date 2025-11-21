using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Foundatio.Tests.Caching;

public abstract partial class CacheClientTestsBase
{
    public virtual async Task AddAsync_WithValidKey_ReturnsTrue(string cacheKey)
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            const string val = "value-should-not-change";

            Assert.True(await cache.AddAsync(cacheKey, val));
            Assert.True(await cache.ExistsAsync(cacheKey));
            Assert.Equal(val, (await cache.GetAsync<string>(cacheKey)).Value);
        }
    }

    public virtual async Task AddAsync_WithExistingKey_ReturnsFalseAndPreservesValue(string cacheKey)
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            const string val = "value-should-not-change";

            Assert.True(await cache.AddAsync(cacheKey, val));

            Assert.False(await cache.AddAsync(cacheKey, "random value"));
            Assert.Equal(val, (await cache.GetAsync<string>(cacheKey)).Value);
        }
    }

    public virtual async Task AddAsync_WithNestedKeyUsingSeparator_StoresCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            const string key = "user:profile";

            Assert.True(await cache.AddAsync(key + ":1", "nested"));
            Assert.True(await cache.ExistsAsync(key + ":1"));
            Assert.Equal("nested", (await cache.GetAsync<string>(key + ":1")).Value);
        }
    }

    public virtual async Task AddAsync_WithConcurrentRequests_OnlyOneSucceeds()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            string cacheKey = Guid.NewGuid().ToString("N").Substring(10);
            long adds = 0;

            await Parallel.ForEachAsync(Enumerable.Range(1, 5), async (i, _) =>
            {
                if (await cache.AddAsync(cacheKey, i, TimeSpan.FromMinutes(1)))
                    Interlocked.Increment(ref adds);
            });

            Assert.Equal(1, adds);
        }
    }

    public virtual async Task AddAsync_WithNullKey_ThrowsArgumentNullException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () => await cache.AddAsync(null, "value"));
        }
    }

    public virtual async Task AddAsync_WithEmptyKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentException>(async () => await cache.AddAsync(String.Empty, "value"));
        }
    }
}
