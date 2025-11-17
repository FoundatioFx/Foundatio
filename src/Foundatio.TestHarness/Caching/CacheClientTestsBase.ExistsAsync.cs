using System.Threading.Tasks;
using Xunit;

namespace Foundatio.Tests.Caching;

public abstract partial class CacheClientTestsBase
{
    public virtual async Task ExistsAsync_WithNullStoredValue_ReturnsTrue()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            SimpleModel nullable = null;
            await cache.SetAsync("nullable", nullable);
            Assert.True(await cache.ExistsAsync("nullable"));

            int? nullableInt = null;
            await cache.SetAsync("nullableInt", nullableInt);
            Assert.True(await cache.ExistsAsync("nullableInt"));
        }
    }

    public virtual async Task ExistsAsync_WithDifferentCasedKeys_ChecksExactMatch()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.SetAsync("orderId", "order123");

            var lowerExists = await cache.ExistsAsync("orderId");
            var titleExists = await cache.ExistsAsync("OrderId");
            var upperExists = await cache.ExistsAsync("ORDERID");

            Assert.True(lowerExists);
            Assert.False(titleExists);
            Assert.False(upperExists);
        }
    }
}
