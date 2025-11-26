using System;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Utility;
using Xunit;

namespace Foundatio.Tests.Caching;

public abstract partial class CacheClientTestsBase
{
    public virtual async Task SetIfHigherAsync_WithDateTime_UpdatesWhenHigher()
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
            Assert.Equal(baseUnixTime, await cache.SetIfHigherAsync("set-if-higher-datetime", baseTime));
            Assert.Equal(baseUnixTime, await cache.GetAsync<long>("set-if-higher-datetime", 0));
            Assert.Equal(baseTime, await cache.GetUnixTimeMillisecondsAsync("set-if-higher-datetime"));

            // Updates when higher
            var higherTime = baseTime + TimeSpan.FromHours(1);
            long higherUnixTime = higherTime.ToUnixTimeMilliseconds();
            Assert.Equal((long)TimeSpan.FromHours(1).TotalMilliseconds,
                await cache.SetIfHigherAsync("set-if-higher-datetime", higherTime));
            Assert.Equal(higherUnixTime, await cache.GetAsync<long>("set-if-higher-datetime", 0));
            Assert.Equal(higherTime, await cache.GetUnixTimeMillisecondsAsync("set-if-higher-datetime"));

            // Does not update when lower
            Assert.Equal(0, await cache.SetIfHigherAsync("set-if-higher-datetime", baseTime));
            Assert.Equal(higherUnixTime, await cache.GetAsync<long>("set-if-higher-datetime", 0));
            Assert.Equal(higherTime, await cache.GetUnixTimeMillisecondsAsync("set-if-higher-datetime"));
        }
    }

    public virtual async Task SetIfHigherAsync_WithLargeNumbers()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            double largeValue = 2 * 1000 * 1000 * 1000;
            double lowerValue = largeValue - 1000;

            await cache.SetAsync("set-if-higher-large", lowerValue);

            Assert.Equal(1000, await cache.SetIfHigherAsync("set-if-higher-large", largeValue));
            Assert.Equal(largeValue, await cache.GetAsync<double>("set-if-higher-large", 0));

            Assert.Equal(0, await cache.SetIfHigherAsync("set-if-higher-large", lowerValue));
            Assert.Equal(largeValue, await cache.GetAsync<double>("set-if-higher-large", 0));
        }
    }
}
