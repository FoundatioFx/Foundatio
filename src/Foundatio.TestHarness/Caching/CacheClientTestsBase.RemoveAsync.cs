using System;
using System.Threading.Tasks;
using Foundatio.Caching;
using Xunit;

namespace Foundatio.Tests.Caching;

public abstract partial class CacheClientTestsBase
{
    public virtual async Task RemoveAsync_WithInvalidKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () => await cache.RemoveAsync(null));
            await Assert.ThrowsAsync<ArgumentException>(async () => await cache.RemoveAsync(String.Empty));
        }
    }

    public virtual async Task RemoveAsync_WithNonExistentKey_ReturnsFalse()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            Assert.False(await cache.RemoveAsync("nonexistent-key"));
            Assert.False(await cache.ExistsAsync("nonexistent-key"));
        }
    }

    public virtual async Task RemoveAsync_WithExpiredKey_KeyDoesNotExist()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.SetAsync("session:expired", "value", TimeSpan.FromMilliseconds(50));
            await Task.Delay(100);

            Assert.False(await cache.RemoveAsync("session:expired"));
            Assert.False(await cache.ExistsAsync("session:expired"));
        }
    }

    public virtual async Task RemoveAsync_WithScopedCache_RemovesOnlyWithinScope()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            var scopedCache1 = new ScopedCacheClient(cache, "scope1");
            var scopedCache2 = new ScopedCacheClient(cache, "scope2");

            await scopedCache1.SetAsync("session:active", 1);
            await scopedCache2.SetAsync("session:active", 2);

            await scopedCache1.RemoveAsync("session:active");

            Assert.False(await scopedCache1.ExistsAsync("session:active"));
            Assert.True(await scopedCache2.ExistsAsync("session:active"));
        }
    }

    public virtual async Task RemoveAsync_WithValidKey_RemovesSuccessfully()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            // Test removing key with value
            Assert.True(await cache.SetAsync("session:active", "value"));
            Assert.True(await cache.ExistsAsync("session:active"));

            Assert.True(await cache.RemoveAsync("session:active"));
            Assert.False(await cache.ExistsAsync("session:active"));
            Assert.False(await cache.RemoveAsync("session:active")); // Already removed

            // Test case sensitivity - only exact match should be removed
            Assert.True(await cache.SetAsync("sessionId", "session1"));
            Assert.True(await cache.SetAsync("SessionId", "session2"));
            Assert.True(await cache.SetAsync("SESSIONID", "session3"));

            Assert.True(await cache.RemoveAsync("SessionId"));
            Assert.False(await cache.RemoveAsync("SessionId")); // Already removed

            Assert.True(await cache.ExistsAsync("sessionId"));
            Assert.False(await cache.ExistsAsync("SessionId"));
            Assert.True(await cache.ExistsAsync("SESSIONID"));
        }
    }
}
