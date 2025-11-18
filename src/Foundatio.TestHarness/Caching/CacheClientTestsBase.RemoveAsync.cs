using System;
using System.Threading.Tasks;
using Foundatio.Caching;
using Xunit;

namespace Foundatio.Tests.Caching;

public abstract partial class CacheClientTestsBase
{
    public virtual async Task RemoveAsync_WithExistingKey_RemovesSuccessfully()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.SetAsync("test", "value");

            Assert.True(await cache.RemoveAsync("test"));
            Assert.False(await cache.RemoveAsync("test"));

            var result = await cache.GetAsync<string>("test");
            Assert.False(result.HasValue);
        }
    }

    public virtual async Task RemoveAsync_WithNonExistentKey_Succeeds()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAsync("nonexistent");

            Assert.False(await cache.ExistsAsync("nonexistent"));
        }
    }

    public virtual async Task RemoveAsync_WithNullValue_RemovesSuccessfully()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.SetAsync("nullable", (string)null);
            Assert.True(await cache.ExistsAsync("nullable"));

            await cache.RemoveAsync("nullable");

            Assert.False(await cache.ExistsAsync("nullable"));
        }
    }

    public virtual async Task RemoveAsync_WithExpiredKey_Succeeds()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.SetAsync("test", "value", TimeSpan.FromMilliseconds(50));
            await Task.Delay(100);

            await cache.RemoveAsync("test");

            Assert.False(await cache.ExistsAsync("test"));
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

            await scopedCache1.SetAsync("test", 1);
            await scopedCache2.SetAsync("test", 2);

            await scopedCache1.RemoveAsync("test");

            Assert.False(await scopedCache1.ExistsAsync("test"));
            Assert.True(await scopedCache2.ExistsAsync("test"));
        }
    }

    public virtual async Task RemoveAsync_MultipleTimes_Succeeds()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.SetAsync("test", "value");

            await cache.RemoveAsync("test");
            await cache.RemoveAsync("test");
            await cache.RemoveAsync("test");

            Assert.False(await cache.ExistsAsync("test"));
        }
    }

    public virtual async Task RemoveAsync_AfterSetAndGet_RemovesCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.SetAsync("test", "value");
            var getValue = await cache.GetAsync<string>("test");
            Assert.True(getValue.HasValue);

            await cache.RemoveAsync("test");

            var result = await cache.GetAsync<string>("test");
            Assert.False(result.HasValue);
        }
    }

    public virtual async Task RemoveAsync_WithNullKey_ThrowsArgumentNullException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () => await cache.RemoveAsync(null));
        }
    }

    public virtual async Task RemoveAsync_WithEmptyKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentException>(async () => await cache.RemoveAsync(String.Empty));
        }
    }

    public virtual async Task RemoveAsync_WithWhitespaceKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentException>(async () => await cache.RemoveAsync("   "));
        }
    }

    public virtual async Task RemoveAsync_WithSpecificCase_RemovesOnlyMatchingKey()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.SetAsync("sessionId", "session1");
            await cache.SetAsync("SessionId", "session2");
            await cache.SetAsync("SESSIONID", "session3");

            await cache.RemoveAsync("SessionId");

            var lower = await cache.GetAsync<string>("sessionId");
            var title = await cache.GetAsync<string>("SessionId");
            var upper = await cache.GetAsync<string>("SESSIONID");

            Assert.True(lower.HasValue);
            Assert.Equal("session1", lower.Value);
            Assert.False(title.HasValue);
            Assert.True(upper.HasValue);
            Assert.Equal("session3", upper.Value);
        }
    }
}
