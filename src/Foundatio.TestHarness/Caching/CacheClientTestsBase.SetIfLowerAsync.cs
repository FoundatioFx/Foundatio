using System;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Utility;
using Xunit;

namespace Foundatio.Tests.Caching;

public abstract partial class CacheClientTestsBase
{
    public virtual async Task SetIfLowerAsync_WithLargeNumbers_UpdatesWhenLower()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            double value = 2 * 1000 * 1000 * 1000;
            await cache.SetAsync("test", value);

            double lowerValue = value - 1000;
            Assert.Equal(1000, await cache.SetIfLowerAsync("test", lowerValue));
            Assert.Equal(lowerValue, await cache.GetAsync<double>("test", 0));

            Assert.Equal(0, await cache.SetIfLowerAsync("test", value));
            Assert.Equal(lowerValue, await cache.GetAsync<double>("test", 0));
        }
    }

    public virtual async Task SetIfLowerAsync_WithDateTime_UpdatesWhenLower()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            DateTime value = DateTime.UtcNow.Floor(TimeSpan.FromMilliseconds(1));
            await cache.SetUnixTimeMillisecondsAsync("test", value);

            var lowerValue = value - TimeSpan.FromHours(1);
            long lowerUnixTimeValue = lowerValue.ToUnixTimeMilliseconds();

            Assert.Equal((long)TimeSpan.FromHours(1).TotalMilliseconds,
                await cache.SetIfLowerAsync("test", lowerValue));
            Assert.Equal(lowerUnixTimeValue, await cache.GetAsync<long>("test", 0));
        }
    }

    public virtual async Task SetIfLowerAsync_WithDateTime_DoesNotUpdateWhenHigher()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            DateTime value = DateTime.UtcNow.Floor(TimeSpan.FromMilliseconds(1));
            long unixTimeValue = value.ToUnixTimeMilliseconds();
            await cache.SetUnixTimeMillisecondsAsync("test", value);

            Assert.Equal(0, await cache.SetIfLowerAsync("test", value.AddHours(1)));
            Assert.Equal(unixTimeValue, await cache.GetAsync<long>("test", 0));
            Assert.Equal(value, await cache.GetUnixTimeMillisecondsAsync("test"));
        }
    }

    public virtual async Task SetIfLowerAsync_WithDateTime_InitializesWhenKeyNotExists()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            DateTime value = DateTime.UtcNow.Floor(TimeSpan.FromMilliseconds(1));
            long unixTimeValue = value.ToUnixTimeMilliseconds();

            Assert.Equal(unixTimeValue, await cache.SetIfLowerAsync("test", value));
            Assert.Equal(unixTimeValue, await cache.GetAsync<long>("test", 0));
            Assert.Equal(value, await cache.GetUnixTimeMillisecondsAsync("test"));
        }
    }
}
