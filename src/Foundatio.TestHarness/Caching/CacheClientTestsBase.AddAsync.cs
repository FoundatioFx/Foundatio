using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Foundatio.Tests.Caching;

public abstract partial class CacheClientTestsBase
{
    public virtual async Task AddAsync_WhenKeyDoesNotExist_AddsValueAndReturnsTrue(string cacheKey)
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            const string initialValue = "initial-value";
            const string duplicateValue = "duplicate-value";

            // Add new key succeeds
            Assert.True(await cache.AddAsync(cacheKey, initialValue));
            Assert.True(await cache.ExistsAsync(cacheKey));
            Assert.Equal(initialValue, (await cache.GetAsync<string>(cacheKey)).Value);

            // Add existing key fails and preserves original value
            Assert.False(await cache.AddAsync(cacheKey, duplicateValue));
            Assert.Equal(initialValue, (await cache.GetAsync<string>(cacheKey)).Value);

            // Nested key with separator works correctly
            string nestedKey = cacheKey + ":nested:child";
            Assert.True(await cache.AddAsync(nestedKey, "nested-value"));
            Assert.True(await cache.ExistsAsync(nestedKey));
            Assert.Equal("nested-value", (await cache.GetAsync<string>(nestedKey)).Value);
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

    public virtual async Task AddAsync_WithInvalidKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () => await cache.AddAsync(null!, "value"));
            await Assert.ThrowsAsync<ArgumentException>(async () => await cache.AddAsync(String.Empty, "value"));
        }
    }
}
