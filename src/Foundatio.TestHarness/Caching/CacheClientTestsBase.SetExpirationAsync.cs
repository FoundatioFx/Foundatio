using System;
using System.Threading.Tasks;
using Foundatio.Caching;
using Xunit;

namespace Foundatio.Tests.Caching;

public abstract partial class CacheClientTestsBase
{
    public virtual async Task SetExpirationAsync_WithDateTimeMinValue_ExpiresImmediately()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            // MinValue should expire items immediately
            Assert.False(await cache.SetAsync("test", 1, DateTime.MinValue));
            Assert.False(await cache.ExistsAsync("test"));
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
            Assert.True(await cache.SetAsync("test", 1, DateTime.MaxValue));
            Assert.Equal(1, (await cache.GetAsync<int>("test")).Value);
            var actualExpiration = await cache.GetExpirationAsync("test");
            Assert.NotNull(actualExpiration);
        }
    }

    public virtual async Task SetExpirationAsync_WithCurrentTime_ExpiresImmediately()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var utcNow = DateTime.UtcNow;

            // Expiration time set to now should expire immediately
            Assert.False(await cache.SetAsync("test", 1, utcNow));
            Assert.False(await cache.ExistsAsync("test"));
            Assert.Null(await cache.GetExpirationAsync("test"));
        }
    }

    public virtual async Task SetExpirationAsync_ChangingFromNoExpirationToFutureTime_UpdatesCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var utcNow = DateTime.UtcNow;

            // Set with no expiration
            Assert.True(await cache.SetAsync("test", 1));
            Assert.Null(await cache.GetExpirationAsync("test"));

            // Update to expire in an hour
            var expiration = utcNow.AddHours(1);
            await cache.SetExpirationAsync("test", expiration);
            var actualExpiration = await cache.GetExpirationAsync("test");
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
            Assert.True(await cache.SetAsync("test", 1, DateTime.UtcNow.AddHours(1)));
            Assert.True(await cache.ExistsAsync("test"));

            // Change expiration to MinValue should remove the key
            await cache.SetExpirationAsync("test", DateTime.MinValue);
            Assert.Null(await cache.GetExpirationAsync("test"));
            Assert.False(await cache.ExistsAsync("test"));
        }
    }

    public virtual async Task SetExpirationAsync_WithNullKey_ThrowsArgumentNullException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await cache.SetExpirationAsync(null, TimeSpan.FromMinutes(1)));
        }
    }

    public virtual async Task SetExpirationAsync_WithEmptyKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentException>(async () =>
                await cache.SetExpirationAsync(String.Empty, TimeSpan.FromMinutes(1)));
        }
    }

    public virtual async Task SetExpirationAsync_WithWhitespaceKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentException>(async () =>
                await cache.SetExpirationAsync("   ", TimeSpan.FromMinutes(1)));
        }
    }

    public virtual async Task SetExpirationAsync_WithDifferentCasedKeys_SetsOnlyExactMatch()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.SetAsync("apiKey", "key1");
            await cache.SetAsync("ApiKey", "key2");
            await cache.SetAsync("APIKEY", "key3");

            var newExpiration = DateTime.UtcNow.AddMinutes(5);
            await cache.SetExpirationAsync("ApiKey", newExpiration);

            var lowerExp = await cache.GetExpirationAsync("apiKey");
            var titleExp = await cache.GetExpirationAsync("ApiKey");
            var upperExp = await cache.GetExpirationAsync("APIKEY");

            Assert.Null(lowerExp);
            Assert.NotNull(titleExp);
            Assert.Null(upperExp);

            var actualExpiration = DateTime.UtcNow.Add(titleExp.Value);
            Assert.True((actualExpiration - newExpiration).TotalSeconds < 2);
        }
    }
}
