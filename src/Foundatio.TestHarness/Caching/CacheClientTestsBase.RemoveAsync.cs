using System;
using System.Threading.Tasks;
using Xunit;

namespace Foundatio.Tests.Caching;

public abstract partial class CacheClientTestsBase
{
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
