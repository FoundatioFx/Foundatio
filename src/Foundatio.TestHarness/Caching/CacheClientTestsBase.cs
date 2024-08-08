using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Utility;
using Foundatio.Xunit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Caching;

public abstract class CacheClientTestsBase : TestWithLoggingBase
{
    protected CacheClientTestsBase(ITestOutputHelper output) : base(output)
    {
    }

    protected virtual ICacheClient GetCacheClient(bool shouldThrowOnSerializationError = true)
    {
        return null;
    }

    public virtual async Task CanGetAllAsync()
    {
        var cache = GetCacheClient();
        if (cache == null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            await cache.SetAsync("test1", 1);
            await cache.SetAsync("test2", 2);
            await cache.SetAsync("test3", 3);
            var result = await cache.GetAllAsync<int>(new[] { "test1", "test2", "test3" });
            Assert.NotNull(result);
            Assert.Equal(3, result.Count);
            Assert.Equal(1, result["test1"].Value);
            Assert.Equal(2, result["test2"].Value);
            Assert.Equal(3, result["test3"].Value);

            await cache.SetAsync("obj1", new SimpleModel { Data1 = "data 1", Data2 = 1 });
            await cache.SetAsync("obj2", new SimpleModel { Data1 = "data 2", Data2 = 2 });
            await cache.SetAsync("obj3", (SimpleModel)null);
            await cache.SetAsync("obj4", new SimpleModel { Data1 = "test 1", Data2 = 4 });

            var result2 = await cache.GetAllAsync<SimpleModel>(new[] { "obj1", "obj2", "obj3", "obj4", "obj5" });
            Assert.NotNull(result2);
            Assert.Equal(5, result2.Count);
            Assert.True(result2["obj3"].IsNull);
            Assert.False(result2["obj5"].HasValue);

            var obj4 = result2["obj4"];
            Assert.NotNull(obj4);
            Assert.Equal("test 1", obj4.Value.Data1);

            await cache.SetAsync("str1", "string 1");
            await cache.SetAsync("str2", "string 2");
            await cache.SetAsync("str3", (string)null);
            var result3 = await cache.GetAllAsync<string>(new[] { "str1", "str2", "str3" });
            Assert.NotNull(result3);
            Assert.Equal(3, result3.Count);
        }
    }

    public virtual async Task CanGetAllWithOverlapAsync()
    {
        var cache = GetCacheClient();
        if (cache == null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            await cache.SetAsync("test1", 1.0);
            await cache.SetAsync("test2", 2.0);
            await cache.SetAsync("test3", 3.0);
            await cache.SetAllAsync(new Dictionary<string, double> {
                { "test3", 3.5 },
                { "test4", 4.0 },
                { "test5", 5.0 }
            });

            var result = await cache.GetAllAsync<double>(new[] { "test1", "test2", "test3", "test4", "test5" });
            Assert.NotNull(result);
            Assert.Equal(5, result.Count);
            Assert.Equal(1.0, result["test1"].Value);
            Assert.Equal(2.0, result["test2"].Value);
            Assert.Equal(3.5, result["test3"].Value);
            Assert.Equal(4.0, result["test4"].Value);
            Assert.Equal(5.0, result["test5"].Value);
        }
    }

    public virtual async Task CanSetAsync()
    {
        var cache = GetCacheClient();
        if (cache == null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            Assert.Equal(3, await cache.ListAddAsync("set", new List<int> { 1, 1, 2, 3 }));
            var result = await cache.GetListAsync<int>("set");
            Assert.NotNull(result);
            Assert.Equal(3, result.Value.Count);

            Assert.True(await cache.ListRemoveAsync("set", 1));
            result = await cache.GetListAsync<int>("set");
            Assert.NotNull(result);
            Assert.Equal(2, result.Value.Count);
        }
    }

    public virtual async Task CanSetAndGetValueAsync()
    {
        var cache = GetCacheClient();
        if (cache == null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            Assert.False((await cache.GetAsync<int>("donkey")).HasValue);
            Assert.False(await cache.ExistsAsync("donkey"));

            SimpleModel nullable = null;
            await cache.SetAsync("nullable", nullable);
            var nullCacheValue = await cache.GetAsync<SimpleModel>("nullable");
            Assert.True(nullCacheValue.HasValue);
            Assert.True(nullCacheValue.IsNull);
            Assert.True(await cache.ExistsAsync("nullable"));

            int? nullableInt = null;
            Assert.False(await cache.ExistsAsync("nullableInt"));
            await cache.SetAsync("nullableInt", nullableInt);
            var nullIntCacheValue = await cache.GetAsync<int?>("nullableInt");
            Assert.True(nullIntCacheValue.HasValue);
            Assert.True(nullIntCacheValue.IsNull);
            Assert.True(await cache.ExistsAsync("nullableInt"));

            await cache.SetAsync("test", 1);
            Assert.Equal(1, (await cache.GetAsync<int>("test")).Value);

            Assert.False(await cache.AddAsync("test", 2));
            Assert.Equal(1, (await cache.GetAsync<int>("test")).Value);

            Assert.True(await cache.ReplaceAsync("test", 2));
            Assert.Equal(2, (await cache.GetAsync<int>("test")).Value);

            Assert.True(await cache.RemoveAsync("test"));
            Assert.False((await cache.GetAsync<int>("test")).HasValue);

            Assert.True(await cache.AddAsync("test", 2));
            Assert.Equal(2, (await cache.GetAsync<int>("test")).Value);

            Assert.True(await cache.ReplaceAsync("test", new MyData { Message = "Testing" }));
            var result = await cache.GetAsync<MyData>("test");
            Assert.NotNull(result);
            Assert.True(result.HasValue);
            Assert.Equal("Testing", result.Value.Message);
        }
    }

    public virtual async Task CanAddAsync()
    {
        var cache = GetCacheClient();
        if (cache == null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            const string key = "type-id";
            const string val = "value-should-not-change";
            Assert.False(await cache.ExistsAsync(key));
            Assert.True(await cache.AddAsync(key, val));
            Assert.True(await cache.ExistsAsync(key));
            Assert.Equal(val, (await cache.GetAsync<string>(key)).Value);

            Assert.False(await cache.AddAsync(key, "random value"));
            Assert.Equal(val, (await cache.GetAsync<string>(key)).Value);

            // Add a sub key
            Assert.True(await cache.AddAsync(key + ":1", "nested"));
            Assert.True(await cache.ExistsAsync(key + ":1"));
            Assert.Equal("nested", (await cache.GetAsync<string>(key + ":1")).Value);
        }
    }

    public virtual async Task CanAddConcurrentlyAsync()
    {
        var cache = GetCacheClient();
        if (cache == null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            string cacheKey = Guid.NewGuid().ToString("N").Substring(10);
            long adds = 0;

            await Parallel.ForEachAsync(Enumerable.Range(1, 5), async (i, _) =>
            {
                if (await cache.AddAsync(cacheKey, i, TimeSpan.FromMinutes(1)))
                    Interlocked.Increment(ref adds);
            });

            Assert.Equal(1, adds);
        }
    }

    public virtual async Task CanGetAsync()
    {
        var cache = GetCacheClient();
        if (cache == null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            await cache.SetAsync<int>("test", 1);
            var cacheValue = await cache.GetAsync<long>("test");
            Assert.True(cacheValue.HasValue);
            Assert.Equal(1L, cacheValue.Value);

            await cache.SetAsync<long>("test", 1);
            var cacheValue2 = await cache.GetAsync<int>("test");
            Assert.True(cacheValue2.HasValue);
            Assert.Equal(1L, cacheValue2.Value);

            await cache.SetAsync<long>("test", Int64.MaxValue);
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                var cacheValue3 = await cache.GetAsync<int>("test");
                Assert.False(cacheValue3.HasValue);
            });

            cacheValue = await cache.GetAsync<long>("test");
            Assert.True(cacheValue.HasValue);
            Assert.Equal(Int64.MaxValue, cacheValue.Value);
        }
    }

    public virtual async Task CanTryGetAsync()
    {
        var cache = GetCacheClient(false);
        if (cache == null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            await cache.SetAsync<int>("test", 1);
            var cacheValue = await cache.GetAsync<long>("test");
            Assert.True(cacheValue.HasValue);
            Assert.Equal(1L, cacheValue.Value);

            await cache.SetAsync<long>("test", 1);
            var cacheValue2 = await cache.GetAsync<int>("test");
            Assert.True(cacheValue2.HasValue);
            Assert.Equal(1L, cacheValue2.Value);

            await cache.SetAsync<long>("test", Int64.MaxValue);
            var cacheValue3 = await cache.GetAsync<int>("test");
            Assert.False(cacheValue3.HasValue);

            cacheValue = await cache.GetAsync<long>("test");
            Assert.True(cacheValue.HasValue);
            Assert.Equal(Int64.MaxValue, cacheValue.Value);

            await cache.SetAsync<MyData>("test", new MyData
            {
                Message = "test"
            });
            cacheValue = await cache.GetAsync<long>("test");
            Assert.False(cacheValue.HasValue);
        }
    }

    public virtual async Task CanUseScopedCachesAsync()
    {
        var cache = GetCacheClient();
        if (cache == null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var scopedCache1 = new ScopedCacheClient(cache, "scoped1");
            var nestedScopedCache1 = new ScopedCacheClient(scopedCache1, "nested");
            var scopedCache2 = new ScopedCacheClient(cache, "scoped2");

            await cache.SetAsync("test", 1);
            await scopedCache1.SetAsync("test", 2);
            await nestedScopedCache1.SetAsync("test", 3);

            Assert.Equal(1, (await cache.GetAsync<int>("test")).Value);
            Assert.Equal(2, (await scopedCache1.GetAsync<int>("test")).Value);
            Assert.Equal(3, (await nestedScopedCache1.GetAsync<int>("test")).Value);

            Assert.Equal(3, (await scopedCache1.GetAsync<int>("nested:test")).Value);
            Assert.Equal(3, (await cache.GetAsync<int>("scoped1:nested:test")).Value);

            // ensure GetAllAsync returns unscoped keys
            Assert.Equal("test", (await scopedCache1.GetAllAsync<int>("test")).Keys.FirstOrDefault());
            Assert.Equal("test", (await nestedScopedCache1.GetAllAsync<int>("test")).Keys.FirstOrDefault());

            await scopedCache2.SetAsync("test", 1);

            int result = await scopedCache1.RemoveByPrefixAsync(String.Empty);
            Assert.Equal(2, result);

            // delete without any matching keys
            result = await scopedCache1.RemoveByPrefixAsync(String.Empty);
            Assert.Equal(0, result);

            Assert.False((await scopedCache1.GetAsync<int>("test")).HasValue);
            Assert.False((await nestedScopedCache1.GetAsync<int>("test")).HasValue);
            Assert.Equal(1, (await cache.GetAsync<int>("test")).Value);
            Assert.Equal(1, (await scopedCache2.GetAsync<int>("test")).Value);

            await scopedCache2.RemoveAllAsync();
            Assert.False((await scopedCache1.GetAsync<int>("test")).HasValue);
            Assert.False((await nestedScopedCache1.GetAsync<int>("test")).HasValue);
            Assert.False((await scopedCache2.GetAsync<int>("test")).HasValue);
            Assert.Equal(1, (await cache.GetAsync<int>("test")).Value);

            Assert.Equal(0, await scopedCache1.GetAsync<double>("total", 0));
            Assert.Equal(10, await scopedCache1.IncrementAsync("total", 10));
            Assert.Equal(10, await scopedCache1.GetAsync<double>("total", 0));

            Assert.Equal(0, await nestedScopedCache1.GetAsync<double>("total", 0));
            Assert.Equal(20, await nestedScopedCache1.IncrementAsync("total", 20));
            Assert.Equal(20, await nestedScopedCache1.GetAsync<double>("total", 0));
            Assert.Equal(1, await nestedScopedCache1.RemoveAllAsync(new[] { "id", "total" }));
            Assert.Equal(0, await nestedScopedCache1.GetAsync<double>("total", 0));

            Assert.Equal(1, await scopedCache1.RemoveAllAsync(new[] { "id", "total" }));
            Assert.Equal(0, await scopedCache1.GetAsync<double>("total", 0));
        }
    }

    public virtual async Task CanRemoveByPrefixAsync()
    {
        var cache = GetCacheClient();
        if (cache == null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            string prefix = "blah:";
            await cache.SetAsync("test", 1);
            await cache.SetAsync(prefix + "test", 1);
            await cache.SetAsync(prefix + "test2", 4);
            Assert.Equal(1, (await cache.GetAsync<int>(prefix + "test")).Value);
            Assert.Equal(1, (await cache.GetAsync<int>("test")).Value);

            Assert.Equal(0, await cache.RemoveByPrefixAsync(prefix + ":doesntexist"));
            Assert.Equal(2, await cache.RemoveByPrefixAsync(prefix));
            Assert.False((await cache.GetAsync<int>(prefix + "test")).HasValue);
            Assert.False((await cache.GetAsync<int>(prefix + "test2")).HasValue);
            Assert.Equal(1, (await cache.GetAsync<int>("test")).Value);

            Assert.Equal(1, await cache.RemoveByPrefixAsync(String.Empty));
        }
    }

    public virtual async Task CanRemoveByPrefixMultipleEntriesAsync(int count)
    {
        var cache = GetCacheClient();
        if (cache == null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string prefix = "prefix:";
            await cache.SetAsync("test", 1);

            await cache.SetAllAsync(Enumerable.Range(0, count).ToDictionary(i => $"{prefix}test{i}"));

            Assert.Equal(1, (await cache.GetAsync<int>($"{prefix}test1")).Value);
            Assert.Equal(1, (await cache.GetAsync<int>("test")).Value);

            Assert.Equal(0, await cache.RemoveByPrefixAsync($"{prefix}:doesntexist"));
            Assert.Equal(count, await cache.RemoveByPrefixAsync(prefix));
        }
    }

    public virtual async Task CanSetAndGetObjectAsync()
    {
        var cache = GetCacheClient();
        if (cache == null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var dt = DateTimeOffset.Now;
            var value = new MyData
            {
                Type = "test",
                Date = dt,
                Message = "Hello World"
            };
            await cache.SetAsync("test", value);
            value.Type = "modified";
            var cachedValue = await cache.GetAsync<MyData>("test");
            Assert.NotNull(cachedValue);
            Assert.Equal(dt, cachedValue.Value.Date);
            Assert.False(value.Equals(cachedValue.Value), "Should not be same reference object");
            Assert.Equal("Hello World", cachedValue.Value.Message);
            Assert.Equal("test", cachedValue.Value.Type);
        }
    }

    public virtual async Task CanSetExpirationAsync()
    {
        var cache = GetCacheClient();
        if (cache == null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var expiresAt = DateTime.UtcNow.AddMilliseconds(300);
            bool success = await cache.SetAsync("test", 1, expiresAt);
            Assert.True(success);
            success = await cache.SetAsync("test2", 1, expiresAt.AddMilliseconds(100));
            Assert.True(success);
            Assert.Equal(1, (await cache.GetAsync<int>("test")).Value);
            Assert.True((await cache.GetExpirationAsync("test")).Value < TimeSpan.FromSeconds(1));

            await Task.Delay(500);
            Assert.False((await cache.GetAsync<int>("test")).HasValue);
            Assert.Null(await cache.GetExpirationAsync("test"));
            Assert.False((await cache.GetAsync<int>("test2")).HasValue);
            Assert.Null(await cache.GetExpirationAsync("test2"));
        }
    }

    public virtual async Task CanSetMinMaxExpirationAsync()
    {
        var cache = GetCacheClient();
        if (cache == null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var timeProvider = new FakeTimeProvider();
            var now = DateTime.UtcNow;
            timeProvider.SetUtcNow(now);

            var expires = DateTime.MaxValue - now.AddDays(1);
            Assert.True(await cache.SetAsync("test1", 1, expires));
            Assert.False(await cache.SetAsync("test2", 1, DateTime.MinValue));
            Assert.True(await cache.SetAsync("test3", 1, DateTime.MaxValue));
            Assert.True(await cache.SetAsync("test4", 1, DateTime.MaxValue - now.AddDays(-1)));

            Assert.Equal(1, (await cache.GetAsync<int>("test1")).Value);
            Assert.InRange((await cache.GetExpirationAsync("test1")).Value, expires.Subtract(TimeSpan.FromSeconds(10)), expires);

            Assert.False(await cache.ExistsAsync("test2"));
            Assert.Equal(1, (await cache.GetAsync<int>("test3")).Value);
            Assert.False((await cache.GetExpirationAsync("test3")).HasValue);
            Assert.Equal(1, (await cache.GetAsync<int>("test4")).Value);
            Assert.False((await cache.GetExpirationAsync("test4")).HasValue);
        }
    }

    public virtual async Task CanIncrementAsync()
    {
        var cache = GetCacheClient();
        if (cache == null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            Assert.True(await cache.SetAsync("test", 0));
            Assert.Equal(1, await cache.IncrementAsync("test"));
            Assert.Equal(1, await cache.IncrementAsync("test1"));
            Assert.Equal(0, await cache.IncrementAsync("test3", 0));

            // The following is not supported by redis.
            if (cache is InMemoryCacheClient)
            {
                Assert.True(await cache.SetAsync("test2", "stringValue"));
                Assert.Equal(1, await cache.IncrementAsync("test2"));
            }
        }
    }

    public virtual async Task CanIncrementAndExpireAsync()
    {
        var cache = GetCacheClient();
        if (cache == null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            bool success = await cache.SetAsync("test", 0);
            Assert.True(success);

            var expiresIn = TimeSpan.FromSeconds(1);
            double newVal = await cache.IncrementAsync("test", 1, expiresIn);

            Assert.Equal(1, newVal);

            await Task.Delay(1500);
            Assert.False((await cache.GetAsync<int>("test")).HasValue);
        }
    }

    public virtual async Task CanReplaceIfEqual()
    {
        var cache = GetCacheClient();
        if (cache == null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            const string cacheKey = "replace-if-equal";
            Assert.True(await cache.AddAsync(cacheKey, "123"));
            var result = await cache.GetAsync<string>(cacheKey);
            Assert.NotNull(result);
            Assert.Equal("123", result.Value);
            Assert.Null(await cache.GetExpirationAsync(cacheKey));

            Assert.False(await cache.ReplaceIfEqualAsync(cacheKey, "456", "789", TimeSpan.FromHours(1)));
            Assert.True(await cache.ReplaceIfEqualAsync(cacheKey, "456", "123", TimeSpan.FromHours(1)));
            result = await cache.GetAsync<string>(cacheKey);
            Assert.NotNull(result);
            Assert.Equal("456", result.Value);
            Assert.NotNull(await cache.GetExpirationAsync(cacheKey));
        }
    }

    public virtual async Task CanRemoveIfEqual()
    {
        var cache = GetCacheClient();
        if (cache == null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            Assert.True(await cache.AddAsync("remove-if-equal", "123"));
            var result = await cache.GetAsync<string>("remove-if-equal");
            Assert.NotNull(result);
            Assert.Equal("123", result.Value);

            Assert.False(await cache.RemoveIfEqualAsync("remove-if-equal", "789"));
            Assert.True(await cache.RemoveIfEqualAsync("remove-if-equal", "123"));
            result = await cache.GetAsync<string>("remove-if-equal");
            Assert.NotNull(result);
            Assert.False(result.HasValue);
        }
    }

    public virtual async Task CanRoundTripLargeNumbersAsync()
    {
        var cache = GetCacheClient();
        if (cache == null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            double value = 2 * 1000 * 1000 * 1000;
            Assert.True(await cache.SetAsync("test", value));
            Assert.Equal(value, await cache.GetAsync<double>("test", 0));

            var lowerValue = value - 1000;
            Assert.Equal(1000, await cache.SetIfLowerAsync("test", lowerValue));
            Assert.Equal(lowerValue, await cache.GetAsync<double>("test", 0));

            Assert.Equal(0, await cache.SetIfLowerAsync("test", value));
            Assert.Equal(lowerValue, await cache.GetAsync<double>("test", 0));

            Assert.Equal(1000, await cache.SetIfHigherAsync("test", value));
            Assert.Equal(value, await cache.GetAsync<double>("test", 0));

            Assert.Equal(0, await cache.SetIfHigherAsync("test", lowerValue));
            Assert.Equal(value, await cache.GetAsync<double>("test", 0));
        }
    }

    public virtual async Task CanGetAndSetDateTimeAsync()
    {
        var cache = GetCacheClient();
        if (cache == null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            DateTime value = DateTime.UtcNow.Floor(TimeSpan.FromSeconds(1));
            long unixTimeValue = value.ToUnixTimeSeconds();
            Assert.True(await cache.SetUnixTimeSecondsAsync("test", value));
            Assert.Equal(unixTimeValue, await cache.GetAsync<long>("test", 0));
            var actual = await cache.GetUnixTimeSecondsAsync("test");
            Assert.Equal(value.Ticks, actual.Ticks);
            Assert.Equal(TimeSpan.Zero, actual.Offset);

            value = DateTime.Now.Floor(TimeSpan.FromMilliseconds(1));
            unixTimeValue = value.ToUnixTimeMilliseconds();
            Assert.True(await cache.SetUnixTimeMillisecondsAsync("test", value));
            Assert.Equal(unixTimeValue, await cache.GetAsync<long>("test", 0));
            actual = (await cache.GetUnixTimeMillisecondsAsync("test")).ToLocalTime();
            Assert.Equal(value.Ticks, actual.Ticks);

            value = DateTime.UtcNow.Floor(TimeSpan.FromMilliseconds(1));
            unixTimeValue = value.ToUnixTimeMilliseconds();
            Assert.True(await cache.SetUnixTimeMillisecondsAsync("test", value));
            Assert.Equal(unixTimeValue, await cache.GetAsync<long>("test", 0));
            actual = await cache.GetUnixTimeMillisecondsAsync("test");
            Assert.Equal(value.Ticks, actual.Ticks);
            Assert.Equal(TimeSpan.Zero, actual.Offset);

            var lowerValue = value - TimeSpan.FromHours(1);
            var lowerUnixTimeValue = lowerValue.ToUnixTimeMilliseconds();
            Assert.Equal((long)TimeSpan.FromHours(1).TotalMilliseconds, await cache.SetIfLowerAsync("test", lowerValue));
            Assert.Equal(lowerUnixTimeValue, await cache.GetAsync<long>("test", 0));

            await cache.RemoveAsync("test");

            Assert.Equal(unixTimeValue, await cache.SetIfLowerAsync("test", value));
            Assert.Equal(unixTimeValue, await cache.GetAsync<long>("test", 0));
            Assert.Equal(value, await cache.GetUnixTimeMillisecondsAsync("test"));

            Assert.Equal(0, await cache.SetIfLowerAsync("test", value.AddHours(1)));
            Assert.Equal(unixTimeValue, await cache.GetAsync<long>("test", 0));
            Assert.Equal(value, await cache.GetUnixTimeMillisecondsAsync("test"));

            await cache.RemoveAsync("test");

            Assert.Equal(unixTimeValue, await cache.SetIfHigherAsync("test", value));
            Assert.Equal(unixTimeValue, await cache.GetAsync<long>("test", 0));
            Assert.Equal(value, await cache.GetUnixTimeMillisecondsAsync("test"));

            Assert.Equal(0, await cache.SetIfHigherAsync("test", value.AddHours(-1)));
            Assert.Equal(unixTimeValue, await cache.GetAsync<long>("test", 0));
            Assert.Equal(value, await cache.GetUnixTimeMillisecondsAsync("test"));

            var higherValue = value + TimeSpan.FromHours(1);
            var higherUnixTimeValue = higherValue.ToUnixTimeMilliseconds();
            Assert.Equal((long)TimeSpan.FromHours(1).TotalMilliseconds, await cache.SetIfHigherAsync("test", higherValue));
            Assert.Equal(higherUnixTimeValue, await cache.GetAsync<long>("test", 0));
            Assert.Equal(higherValue, await cache.GetUnixTimeMillisecondsAsync("test"));
        }
    }

    public virtual async Task CanRoundTripLargeNumbersWithExpirationAsync()
    {
        var cache = GetCacheClient();
        if (cache == null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var minExpiration = TimeSpan.FromHours(1).Add(TimeSpan.FromMinutes(59)).Add(TimeSpan.FromSeconds(55));
            double value = 2 * 1000 * 1000 * 1000;
            Assert.True(await cache.SetAsync("test", value, TimeSpan.FromHours(2)));
            Assert.Equal(value, await cache.GetAsync<double>("test", 0));
            Assert.InRange((await cache.GetExpirationAsync("test")).Value, minExpiration, TimeSpan.FromHours(2));

            var lowerValue = value - 1000;
            Assert.Equal(1000, await cache.SetIfLowerAsync("test", lowerValue, TimeSpan.FromHours(2)));
            Assert.Equal(lowerValue, await cache.GetAsync<double>("test", 0));
            Assert.InRange((await cache.GetExpirationAsync("test")).Value, minExpiration, TimeSpan.FromHours(2));

            Assert.Equal(0, await cache.SetIfLowerAsync("test", value, TimeSpan.FromHours(2)));
            Assert.Equal(lowerValue, await cache.GetAsync<double>("test", 0));
            Assert.InRange((await cache.GetExpirationAsync("test")).Value, minExpiration, TimeSpan.FromHours(2));

            Assert.Equal(1000, await cache.SetIfHigherAsync("test", value, TimeSpan.FromHours(2)));
            Assert.Equal(value, await cache.GetAsync<double>("test", 0));
            Assert.InRange((await cache.GetExpirationAsync("test")).Value, minExpiration, TimeSpan.FromHours(2));

            Assert.Equal(0, await cache.SetIfHigherAsync("test", lowerValue, TimeSpan.FromHours(2)));
            Assert.Equal(value, await cache.GetAsync<double>("test", 0));
            Assert.InRange((await cache.GetExpirationAsync("test")).Value, minExpiration, TimeSpan.FromHours(2));
        }
    }

    public virtual async Task CanManageListsAsync()
    {
        var cache = GetCacheClient();
        if (cache == null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            await Assert.ThrowsAsync<ArgumentNullException>(() => cache.ListAddAsync(null, 1));
            await Assert.ThrowsAsync<ArgumentNullException>(() => cache.ListAddAsync(String.Empty, 1));

            await Assert.ThrowsAsync<ArgumentNullException>(() => cache.ListRemoveAsync(null, 1));
            await Assert.ThrowsAsync<ArgumentNullException>(() => cache.ListRemoveAsync(String.Empty, 1));

            await Assert.ThrowsAsync<ArgumentNullException>(() => cache.GetListAsync<ICollection<int>>(null));
            await Assert.ThrowsAsync<ArgumentNullException>(() => cache.GetListAsync<ICollection<int>>(String.Empty));

            await cache.ListAddAsync("test1", new[] { 1, 2, 3 });
            var result = await cache.GetListAsync<int>("test1");
            Assert.NotNull(result);
            Assert.Equal(3, result.Value.Count);

            await cache.ListRemoveAsync("test1", new[] { 1, 2, 3 });
            result = await cache.GetListAsync<int>("test1");
            Assert.NotNull(result);
            Assert.Empty(result.Value);

            // test single strings don't get handled as char arrays
            await cache.RemoveAllAsync();

            await cache.ListAddAsync("stringlist", "myvalue");
            var stringResult = await cache.GetListAsync<string>("stringlist");
            Assert.Single(stringResult.Value);
            Assert.Equal("myvalue", stringResult.Value.First());

            await cache.ListRemoveAsync("stringlist", "myvalue");
            stringResult = await cache.GetListAsync<string>("stringlist");
            Assert.Empty(stringResult.Value);

            await cache.RemoveAllAsync();

            await cache.ListAddAsync("test1", 1);
            await cache.ListAddAsync("test1", 2);
            await cache.ListAddAsync("test1", 3);
            result = await cache.GetListAsync<int>("test1");
            Assert.NotNull(result);
            Assert.Equal(3, result.Value.Count);

            await cache.ListRemoveAsync("test1", 2);
            result = await cache.GetListAsync<int>("test1");
            Assert.NotNull(result);
            Assert.Equal(2, result.Value.Count);

            await cache.ListRemoveAsync("test1", 1);
            await cache.ListRemoveAsync("test1", 3);
            result = await cache.GetListAsync<int>("test1");
            Assert.NotNull(result);
            Assert.Empty(result.Value);

            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                await cache.AddAsync("key1", 1);
                await cache.ListAddAsync("key1", 1);
            });

            // test paging through items in list
            await cache.ListAddAsync("testpaging", new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 });
            var pagedResult = await cache.GetListAsync<int>("testpaging", 1, 5);
            Assert.NotNull(pagedResult);
            Assert.Equal(5, pagedResult.Value.Count);
            Assert.Equal(pagedResult.Value.ToArray(), new[] { 1, 2, 3, 4, 5 });

            pagedResult = await cache.GetListAsync<int>("testpaging", 2, 5);
            Assert.NotNull(pagedResult);
            Assert.Equal(5, pagedResult.Value.Count);
            Assert.Equal(pagedResult.Value.ToArray(), new[] { 6, 7, 8, 9, 10 });

            await cache.ListAddAsync("testpaging", new[] { 21, 22 });

            pagedResult = await cache.GetListAsync<int>("testpaging", 5, 5);
            Assert.NotNull(pagedResult);
            Assert.Equal(2, pagedResult.Value.Count);
            Assert.Equal(pagedResult.Value.ToArray(), new[] { 21, 22 });

            await cache.ListRemoveAsync("testpaging", 2);
            pagedResult = await cache.GetListAsync<int>("testpaging", 1, 5);
            Assert.NotNull(pagedResult);
            Assert.Equal(5, pagedResult.Value.Count);
            Assert.Equal(pagedResult.Value.ToArray(), new[] { 1, 3, 4, 5, 6 });
        }
    }

    public virtual async Task MeasureThroughputAsync()
    {
        var cache = GetCacheClient();
        if (cache == null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var sw = Stopwatch.StartNew();
            const int itemCount = 10000;
            for (int i = 0; i < itemCount; i++)
            {
                await cache.SetAsync("test", 13422);
                await cache.SetAsync("flag", true);
                Assert.Equal(13422, (await cache.GetAsync<int>("test")).Value);
                Assert.Null(await cache.GetAsync<int>("test2"));
                Assert.True((await cache.GetAsync<bool>("flag")).Value);
            }
            sw.Stop();
            _logger.LogInformation("Time: {0}ms", sw.ElapsedMilliseconds);
        }
    }

    public virtual async Task MeasureSerializerSimpleThroughputAsync()
    {
        var cache = GetCacheClient();
        if (cache == null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var sw = Stopwatch.StartNew();
            const int itemCount = 10000;
            for (int i = 0; i < itemCount; i++)
            {
                await cache.SetAsync("test", new SimpleModel
                {
                    Data1 = "Hello",
                    Data2 = 12
                });
                var model = await cache.GetAsync<SimpleModel>("test");
                Assert.True(model.HasValue);
                Assert.Equal("Hello", model.Value.Data1);
                Assert.Equal(12, model.Value.Data2);
            }
            sw.Stop();
            _logger.LogInformation("Time: {0}ms", sw.ElapsedMilliseconds);
        }
    }

    public virtual async Task MeasureSerializerComplexThroughputAsync()
    {
        var cache = GetCacheClient();
        if (cache == null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var sw = Stopwatch.StartNew();
            const int itemCount = 10000;
            for (int i = 0; i < itemCount; i++)
            {
                await cache.SetAsync("test", new ComplexModel
                {
                    Data1 = "Hello",
                    Data2 = 12,
                    Data3 = true,
                    Simple = new SimpleModel
                    {
                        Data1 = "hi",
                        Data2 = 13
                    },
                    Simples = new List<SimpleModel> {
                        new SimpleModel {
                            Data1 = "hey",
                            Data2 = 45
                        },
                        new SimpleModel {
                            Data1 = "next",
                            Data2 = 3423
                        }
                    },
                    DictionarySimples = new Dictionary<string, SimpleModel> {
                        { "sdf", new SimpleModel { Data1 = "Sachin" } }
                    },

                    DerivedDictionarySimples = new SampleDictionary<string, SimpleModel> {
                        { "sdf", new SimpleModel { Data1 = "Sachin" } }
                    }
                });

                var model = await cache.GetAsync<ComplexModel>("test");
                Assert.True(model.HasValue);
                Assert.Equal("Hello", model.Value.Data1);
                Assert.Equal(12, model.Value.Data2);
            }
            sw.Stop();
            _logger.LogInformation("Time: {0}ms", sw.ElapsedMilliseconds);
        }
    }
}

public class SimpleModel
{
    public string Data1 { get; set; }
    public int Data2 { get; set; }
}

public class ComplexModel
{
    public string Data1 { get; set; }
    public int Data2 { get; set; }
    public SimpleModel Simple { get; set; }
    public ICollection<SimpleModel> Simples { get; set; }
    public bool Data3 { get; set; }
    public IDictionary<string, SimpleModel> DictionarySimples { get; set; }
    public SampleDictionary<string, SimpleModel> DerivedDictionarySimples { get; set; }
}

public class MyData
{
    private readonly string _blah = "blah";
    public string Blah => _blah;
    public string Type { get; set; }
    public DateTimeOffset Date { get; set; }
    public string Message { get; set; }
}

public class SampleDictionary<TKey, TValue> : IDictionary<TKey, TValue>
{
    private readonly IDictionary<TKey, TValue> _dictionary;

    public SampleDictionary()
    {
        _dictionary = new Dictionary<TKey, TValue>();
    }

    public SampleDictionary(IDictionary<TKey, TValue> dictionary)
    {
        _dictionary = new Dictionary<TKey, TValue>(dictionary);
    }

    public SampleDictionary(IEqualityComparer<TKey> comparer)
    {
        _dictionary = new Dictionary<TKey, TValue>(comparer);
    }

    public SampleDictionary(IDictionary<TKey, TValue> dictionary, IEqualityComparer<TKey> comparer)
    {
        _dictionary = new Dictionary<TKey, TValue>(dictionary, comparer);
    }

    public void Add(TKey key, TValue value)
    {
        _dictionary.Add(key, value);
    }

    public void Add(KeyValuePair<TKey, TValue> item)
    {
        _dictionary.Add(item);
    }

    public bool Remove(TKey key)
    {
        return _dictionary.Remove(key);
    }

    public bool Remove(KeyValuePair<TKey, TValue> item)
    {
        return _dictionary.Remove(item);
    }

    public void Clear()
    {
        _dictionary.Clear();
    }

    public bool ContainsKey(TKey key)
    {
        return _dictionary.ContainsKey(key);
    }

    public bool Contains(KeyValuePair<TKey, TValue> item)
    {
        return _dictionary.Contains(item);
    }

    public bool TryGetValue(TKey key, out TValue value)
    {
        return _dictionary.TryGetValue(key, out value);
    }

    public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
    {
        _dictionary.CopyTo(array, arrayIndex);
    }

    public ICollection<TKey> Keys => _dictionary.Keys;

    public ICollection<TValue> Values => _dictionary.Values;

    public int Count => _dictionary.Count;

    public bool IsReadOnly => _dictionary.IsReadOnly;

    public TValue this[TKey key]
    {
        get => _dictionary[key];
        set => _dictionary[key] = value;
    }

    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
    {
        return _dictionary.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}
