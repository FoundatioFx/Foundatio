using System;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Utility;
using Xunit;

namespace Foundatio.Tests.Caching;

public abstract partial class CacheClientTestsBase
{
    public virtual async Task SetIfHigherAsync_WithLargeNumbers_UpdatesWhenHigher()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            double value = 2 * 1000 * 1000 * 1000;
            double lowerValue = value - 1000;
            await cache.SetAsync("test", lowerValue);

            Assert.Equal(1000, await cache.SetIfHigherAsync("test", value));
            Assert.Equal(value, await cache.GetAsync<double>("test", 0));

            Assert.Equal(0, await cache.SetIfHigherAsync("test", lowerValue));
            Assert.Equal(value, await cache.GetAsync<double>("test", 0));
        }
    }

    public virtual async Task SetIfHigherAsync_WithDateTime_UpdatesWhenHigher()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            DateTime value = DateTime.UtcNow.Floor(TimeSpan.FromMilliseconds(1));
            await cache.SetUnixTimeMillisecondsAsync("test", value);

            var higherValue = value + TimeSpan.FromHours(1);
            long higherUnixTimeValue = higherValue.ToUnixTimeMilliseconds();

            Assert.Equal((long)TimeSpan.FromHours(1).TotalMilliseconds,
                await cache.SetIfHigherAsync("test", higherValue));
            Assert.Equal(higherUnixTimeValue, await cache.GetAsync<long>("test", 0));
            Assert.Equal(higherValue, await cache.GetUnixTimeMillisecondsAsync("test"));
        }
    }

    public virtual async Task SetIfHigherAsync_WithDateTime_DoesNotUpdateWhenLower()
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

            Assert.Equal(0, await cache.SetIfHigherAsync("test", value.AddHours(-1)));
            Assert.Equal(unixTimeValue, await cache.GetAsync<long>("test", 0));
            Assert.Equal(value, await cache.GetUnixTimeMillisecondsAsync("test"));
        }
    }

    public virtual async Task SetIfHigherAsync_WithDateTime_InitializesWhenKeyNotExists()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            DateTime value = DateTime.UtcNow.Floor(TimeSpan.FromMilliseconds(1));
            long unixTimeValue = value.ToUnixTimeMilliseconds();

            Assert.Equal(unixTimeValue, await cache.SetIfHigherAsync("test", value));
            Assert.Equal(unixTimeValue, await cache.GetAsync<long>("test", 0));
            Assert.Equal(value, await cache.GetUnixTimeMillisecondsAsync("test"));
        }
    }
}
