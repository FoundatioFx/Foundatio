using System;
using System.Threading.Tasks;
using Foundatio.Caching;
using Xunit;

namespace Foundatio.Tests.Caching;

public abstract partial class CacheClientTestsBase
{
    public virtual async Task GetExpirationAsync_WithNoExpiration_ReturnsNull()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            await cache.SetAsync("no-expiration", "value");
            var expiration = await cache.GetExpirationAsync("no-expiration");

            Assert.Null(expiration);
        }
    }

    public virtual async Task GetExpirationAsync_WithExpiration_ReturnsCorrectTimeSpan()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var expiresAt = DateTime.UtcNow.AddHours(1);
            await cache.SetAsync("with-expiration", "value", expiresAt);
            var expiration = await cache.GetExpirationAsync("with-expiration");

            Assert.NotNull(expiration);
            Assert.InRange(expiration.Value, TimeSpan.FromMinutes(59),
                TimeSpan.FromHours(1).Add(TimeSpan.FromSeconds(10)));
        }
    }

    public virtual async Task GetExpirationAsync_WithNonExistentKey_ReturnsNull()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var expiration = await cache.GetExpirationAsync("non-existent-key");

            Assert.Null(expiration);
        }
    }

    public virtual async Task GetExpirationAsync_WithExpiredKey_ReturnsNull()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var quickExpiry = DateTime.UtcNow.AddMilliseconds(100);
            await cache.SetAsync("quick-expiry", "value", quickExpiry);
            await Task.Delay(200);
            var expiration = await cache.GetExpirationAsync("quick-expiry");

            Assert.Null(expiration);
        }
    }

    public virtual async Task GetExpirationAsync_AfterExpiry_ReturnsNull()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var expiresAt = DateTime.UtcNow.AddMilliseconds(300);
            await cache.SetAsync("test", 1, expiresAt);

            await Task.Delay(500);
            var expiration = await cache.GetExpirationAsync("test");

            Assert.Null(expiration);
        }
    }

    public virtual async Task GetExpirationAsync_WithNullKey_ThrowsArgumentNullException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () => await cache.GetExpirationAsync(null));
        }
    }

    public virtual async Task GetExpirationAsync_WithEmptyKey_ThrowsArgumentNullException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () => await cache.GetExpirationAsync(String.Empty));
        }
    }

    public virtual async Task GetExpirationAsync_WithWhitespaceKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () => await cache.GetExpirationAsync("   "));
        }
    }

    public virtual async Task GetExpirationAsync_WithDifferentCasedKeys_GetsExactMatch()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            var expiration1 = DateTime.UtcNow.AddMinutes(10);
            var expiration2 = DateTime.UtcNow.AddMinutes(20);
            var expiration3 = DateTime.UtcNow.AddMinutes(30);

            await cache.SetAsync("tokenId", "token1", expiration1);
            await cache.SetAsync("TokenId", "token2", expiration2);
            await cache.SetAsync("TOKENID", "token3", expiration3);

            var lowerExp = await cache.GetExpirationAsync("tokenId");
            var titleExp = await cache.GetExpirationAsync("TokenId");
            var upperExp = await cache.GetExpirationAsync("TOKENID");

            Assert.NotNull(lowerExp);
            Assert.NotNull(titleExp);
            Assert.NotNull(upperExp);

            var actualExpiration1 = DateTime.UtcNow.Add(lowerExp.Value);
            var actualExpiration2 = DateTime.UtcNow.Add(titleExp.Value);
            var actualExpiration3 = DateTime.UtcNow.Add(upperExp.Value);
            Assert.True((actualExpiration1 - expiration1).TotalSeconds < 2);
            Assert.True((actualExpiration2 - expiration2).TotalSeconds < 2);
            Assert.True((actualExpiration3 - expiration3).TotalSeconds < 2);
        }
    }
}
