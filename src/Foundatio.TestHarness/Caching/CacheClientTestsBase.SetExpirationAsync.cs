using System;
using System.Threading.Tasks;
using Foundatio.Caching;
using Xunit;

namespace Foundatio.Tests.Caching;

public abstract partial class CacheClientTestsBase
{
    public virtual async Task SetExpirationAsync_ChangingFromNoExpirationToFutureTime_UpdatesCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var utcNow = DateTime.UtcNow;
            const string cacheKey = "token:refresh";

            // Set with no expiration
            Assert.True(await cache.SetAsync(cacheKey, 1));
            Assert.Null(await cache.GetExpirationAsync(cacheKey));

            // Update to expire in an hour
            var expiration = utcNow.AddHours(1);
            await cache.SetExpirationAsync(cacheKey, expiration);
            var actualExpiration = await cache.GetExpirationAsync(cacheKey);
            Assert.NotNull(actualExpiration);
            Assert.InRange(actualExpiration.Value, expiration - expiration.Subtract(TimeSpan.FromSeconds(5)),
                expiration - utcNow);
        }
    }

    public virtual async Task SetExpirationAsync_ChangingToDateTimeMinValue_RemovesKey()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            // Set with future expiration
            Assert.True(await cache.SetAsync("expiration-test", 1, DateTime.UtcNow.AddHours(1)));
            Assert.True(await cache.ExistsAsync("expiration-test"));

            // Change expiration to MinValue should remove the key
            await cache.SetExpirationAsync("expiration-test", DateTime.MinValue);
            Assert.Null(await cache.GetExpirationAsync("expiration-test"));
            Assert.False(await cache.ExistsAsync("expiration-test"));
        }
    }

    public virtual async Task SetExpirationAsync_WithDateTimeMaxValue_NeverExpires()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            // MaxValue should never expire
            Assert.True(await cache.SetAsync("max-expiration", 1, DateTime.MaxValue));
            Assert.Equal(1, (await cache.GetAsync<int>("max-expiration")).Value);
            var actualExpiration = await cache.GetExpirationAsync("max-expiration");
            Assert.NotNull(actualExpiration);
        }
    }

    public virtual async Task SetExpirationAsync_WithInvalidKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await cache.SetExpirationAsync(null, TimeSpan.FromMinutes(1)));

            await Assert.ThrowsAsync<ArgumentException>(async () =>
                await cache.SetExpirationAsync(String.Empty, TimeSpan.FromMinutes(1)));
        }
    }

    public virtual async Task SetExpirationAsync_WithPastOrCurrentTime_ExpiresImmediately()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var utcNow = DateTime.UtcNow;

            // MinValue should return false and not add the key
            Assert.False(await cache.SetAsync("min-value-key", 1, DateTime.MinValue));
            Assert.False(await cache.ExistsAsync("min-value-key"));

            // Current time (captured earlier) should return false and not add the key
            // IsExpired uses < comparison, so by the time SetAsync runs, utcNow is in the past
            Assert.False(await cache.SetAsync("current-time-key", 1, utcNow));
            Assert.False(await cache.ExistsAsync("current-time-key"));
            Assert.Null(await cache.GetExpirationAsync("current-time-key"));
        }
    }
}
