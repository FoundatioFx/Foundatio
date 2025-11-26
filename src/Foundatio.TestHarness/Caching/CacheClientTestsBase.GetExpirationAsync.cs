using System;
using System.Threading.Tasks;
using Foundatio.Caching;
using Xunit;

namespace Foundatio.Tests.Caching;

public abstract partial class CacheClientTestsBase
{
    public virtual async Task GetExpirationAsync_WithVariousKeyStates_ReturnsExpectedExpiration()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            const string cacheKey = "test-expiration-key";

            // Non-existent key returns null
            var expiration = await cache.GetExpirationAsync(cacheKey);
            Assert.Null(expiration);

            // Key without expiration returns null
            await cache.SetAsync(cacheKey, "value");
            expiration = await cache.GetExpirationAsync(cacheKey);
            Assert.Null(expiration);

            // Key with expiration returns correct TimeSpan
            await cache.SetAsync(cacheKey, "value", DateTime.UtcNow.AddHours(1));
            expiration = await cache.GetExpirationAsync(cacheKey);
            Assert.NotNull(expiration);
            Assert.InRange(expiration.Value, TimeSpan.FromMinutes(59),
                TimeSpan.FromHours(1).Add(TimeSpan.FromSeconds(10)));

            // Expired key returns null
            await cache.RemoveAsync(cacheKey);
            await cache.SetAsync(cacheKey, 1, DateTime.UtcNow.AddMilliseconds(50));

            await Task.Delay(100);

            expiration = await cache.GetExpirationAsync(cacheKey);
            Assert.Null(expiration);
        }
    }

    public virtual async Task GetExpirationAsync_WithInvalidKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () => await cache.GetExpirationAsync(null));
            await Assert.ThrowsAsync<ArgumentException>(async () => await cache.GetExpirationAsync(String.Empty));
        }
    }
}
