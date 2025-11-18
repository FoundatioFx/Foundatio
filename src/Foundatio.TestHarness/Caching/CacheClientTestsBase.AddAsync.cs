using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Foundatio.Tests.Caching;

public abstract partial class CacheClientTestsBase
{
    public virtual async Task AddAsync_WithNewKey_ReturnsTrue()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            const string key = "type-id";
            const string val = "value-should-not-change";

            Assert.False(await cache.ExistsAsync(key));
            Assert.True(await cache.AddAsync(key, val));
            Assert.True(await cache.ExistsAsync(key));
            Assert.Equal(val, (await cache.GetAsync<string>(key)).Value);
        }
    }

    public virtual async Task AddAsync_WithExistingKey_ReturnsFalseAndPreservesValue()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            const string key = "type-id";
            const string val = "value-should-not-change";
            await cache.AddAsync(key, val);

            Assert.False(await cache.AddAsync(key, "random value"));
            Assert.Equal(val, (await cache.GetAsync<string>(key)).Value);
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

            const string key = "type-id";

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

    public virtual async Task AddAsync_WithWhitespaceKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentException>(async () => await cache.AddAsync("   ", "value"));
        }
    }
}
