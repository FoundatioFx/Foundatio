using System;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Utility;
using Xunit;

namespace Foundatio.Tests.Caching;

public abstract partial class CacheClientTestsBase
{
    public virtual async Task SetIfLowerAsync_WithDateTime_UpdatesWhenLower()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var baseTime = DateTime.UtcNow.Floor(TimeSpan.FromMilliseconds(1));
            long baseUnixTime = baseTime.ToUnixTimeMilliseconds();

            // Initializes when key doesn't exist
            Assert.Equal(baseUnixTime, await cache.SetIfLowerAsync("set-if-lower-datetime", baseTime));
            Assert.Equal(baseUnixTime, await cache.GetAsync<long>("set-if-lower-datetime", 0));
            Assert.Equal(baseTime, await cache.GetUnixTimeMillisecondsAsync("set-if-lower-datetime"));

            // Updates when lower
            var lowerTime = baseTime - TimeSpan.FromHours(1);
            long lowerUnixTime = lowerTime.ToUnixTimeMilliseconds();
            Assert.Equal((long)TimeSpan.FromHours(1).TotalMilliseconds,
                await cache.SetIfLowerAsync("set-if-lower-datetime", lowerTime));
            Assert.Equal(lowerUnixTime, await cache.GetAsync<long>("set-if-lower-datetime", 0));
            Assert.Equal(lowerTime, await cache.GetUnixTimeMillisecondsAsync("set-if-lower-datetime"));

            // Does not update when higher
            Assert.Equal(0, await cache.SetIfLowerAsync("set-if-lower-datetime", baseTime));
            Assert.Equal(lowerUnixTime, await cache.GetAsync<long>("set-if-lower-datetime", 0));
            Assert.Equal(lowerTime, await cache.GetUnixTimeMillisecondsAsync("set-if-lower-datetime"));
        }
    }

    public virtual async Task SetIfLowerAsync_WithLargeNumbers()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            double largeValue = 2 * 1000 * 1000 * 1000;
            double lowerValue = largeValue - 1000;

            await cache.SetAsync("set-if-lower-large", largeValue);

            Assert.Equal(1000, await cache.SetIfLowerAsync("set-if-lower-large", lowerValue));
            Assert.Equal(lowerValue, await cache.GetAsync<double>("set-if-lower-large", 0));

            Assert.Equal(0, await cache.SetIfLowerAsync("set-if-lower-large", largeValue));
            Assert.Equal(lowerValue, await cache.GetAsync<double>("set-if-lower-large", 0));
        }
    }
}
