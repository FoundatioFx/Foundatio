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
using Xunit;

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

    public virtual async Task AddAsync_WhenKeyDoesNotExist_AddsValueAndReturnsTrue(string cacheKey)
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            const string initialValue = "initial-value";
            const string duplicateValue = "duplicate-value";

            // Add new key succeeds
            Assert.True(await cache.AddAsync(cacheKey, initialValue));
            Assert.True(await cache.ExistsAsync(cacheKey));
            Assert.Equal(initialValue, (await cache.GetAsync<string>(cacheKey)).Value);

            // Add existing key fails and preserves original value
            Assert.False(await cache.AddAsync(cacheKey, duplicateValue));
            Assert.Equal(initialValue, (await cache.GetAsync<string>(cacheKey)).Value);

            // Nested key with separator works correctly
            string nestedKey = cacheKey + ":nested:child";
            Assert.True(await cache.AddAsync(nestedKey, "nested-value"));
            Assert.True(await cache.ExistsAsync(nestedKey));
            Assert.Equal("nested-value", (await cache.GetAsync<string>(nestedKey)).Value);
        }
    }

    public virtual async Task AddAsync_WithConcurrentRequests_OnlyOneSucceeds()
    {
        var cache = GetCacheClient();
        if (cache is null)
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

    public virtual async Task AddAsync_WithInvalidKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () => await cache.AddAsync(null!, "value"));
            await Assert.ThrowsAsync<ArgumentException>(async () => await cache.AddAsync(String.Empty, "value"));
        }
    }

    public virtual async Task AddAsync_WithExpiration_SetsExpirationCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            // Past expiration: should return false and key should not exist
            Assert.False(await cache.AddAsync("add-past-exp", "value", TimeSpan.FromMilliseconds(-1)));
            Assert.False(await cache.ExistsAsync("add-past-exp"));
            Assert.False((await cache.GetAsync<string>("add-past-exp")).HasValue);

            // Past expiration on existing key: should return false and remove the key
            Assert.True(await cache.AddAsync("add-past-exp-existing", "original"));
            Assert.True(await cache.ExistsAsync("add-past-exp-existing"));
            Assert.False(await cache.AddAsync("add-past-exp-existing", "new-value", TimeSpan.FromMilliseconds(-1)));
            Assert.False(await cache.ExistsAsync("add-past-exp-existing"));
            Assert.False((await cache.GetAsync<string>("add-past-exp-existing")).HasValue);

            // Zero expiration: should also be treated as expired
            Assert.False(await cache.AddAsync("add-zero-exp", "value", TimeSpan.Zero));
            Assert.False(await cache.ExistsAsync("add-zero-exp"));
            Assert.False((await cache.GetAsync<string>("add-zero-exp")).HasValue);

            // Max expiration: should return true, key should exist with no expiration (null)
            Assert.True(await cache.AddAsync("add-max-exp", "value", TimeSpan.MaxValue));
            Assert.True(await cache.ExistsAsync("add-max-exp"));
            Assert.Equal("value", (await cache.GetAsync<string>("add-max-exp")).Value);
            var expiration = await cache.GetExpirationAsync("add-max-exp");
            Assert.Null(expiration); // TimeSpan.MaxValue means no expiration

            // Normal expiration: should return true, key should exist with correct expiration
            Assert.True(await cache.AddAsync("add-normal-exp", "value", TimeSpan.FromHours(1)));
            Assert.True(await cache.ExistsAsync("add-normal-exp"));
            Assert.Equal("value", (await cache.GetAsync<string>("add-normal-exp")).Value);
            expiration = await cache.GetExpirationAsync("add-normal-exp");
            Assert.NotNull(expiration);
            Assert.InRange(expiration.Value, TimeSpan.FromMinutes(59), TimeSpan.FromHours(1));

            // Note: AddAsync with null expiration on existing key returns false (key exists)
            // so we can't test expiration removal via AddAsync - use SetAsync for that scenario
        }
    }

    public virtual async Task ExistsAsync_WithVariousKeys_ReturnsCorrectExistenceStatus()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            // Non-existent key returns false
            Assert.False(await cache.ExistsAsync("nonexistent"));

            // Existing key returns true
            await cache.SetAsync("test", 123);
            Assert.True(await cache.ExistsAsync("test"));

            // Case-sensitivity check
            await cache.SetAsync("orderId", "order123");
            Assert.True(await cache.ExistsAsync("orderId"));
            Assert.False(await cache.ExistsAsync("OrderId"));
            Assert.False(await cache.ExistsAsync("ORDERID"));

            // Null stored value still exists
            SimpleModel nullable = null;
            await cache.SetAsync("nullable", nullable);
            Assert.True(await cache.ExistsAsync("nullable"));

            int? nullableInt = null;
            await cache.SetAsync("nullableInt", nullableInt);
            Assert.True(await cache.ExistsAsync("nullableInt"));
        }
    }

    public virtual async Task ExistsAsync_WithExpiredKey_ReturnsFalse()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.SetAsync("test", "value", TimeSpan.FromMilliseconds(50));
            Assert.True(await cache.ExistsAsync("test"));

            await Task.Delay(100);

            Assert.False(await cache.ExistsAsync("test"));
        }
    }

    public virtual async Task ExistsAsync_WithScopedCache_ChecksOnlyWithinScope()
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

            Assert.True(await scopedCache1.ExistsAsync("test"));
            Assert.True(await scopedCache2.ExistsAsync("test"));
            Assert.False(await cache.ExistsAsync("test"));
        }
    }

    public virtual async Task ExistsAsync_WithInvalidKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await cache.ExistsAsync(null));

            await Assert.ThrowsAsync<ArgumentException>(async () =>
                await cache.ExistsAsync(String.Empty));
        }
    }

    public virtual async Task GetAllExpirationAsync_WithMixedKeys_ReturnsExpectedResults()
    {
        // Arrange
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            // Set up keys with various states:
            // - expired-key: will expire before we query
            // - valid-key: has expiration, will be returned
            // - no-expiration-key: no expiration, should not be returned
            // - nonexistent-key: never created, should not be returned
            await cache.SetAsync("expired-key", 1, TimeSpan.FromMilliseconds(50));
            await cache.SetAsync("valid-key", 2, TimeSpan.FromMinutes(10));
            await cache.SetAsync("no-expiration-key", 3);

            // Wait for expired-key to expire
            await Task.Delay(100);

            // Act
            var expirations = await cache.GetAllExpirationAsync(["expired-key", "valid-key", "no-expiration-key", "nonexistent-key"]);

            // Assert
            Assert.NotNull(expirations);
            Assert.Single(expirations); // Only valid-key should be returned

            Assert.False(expirations.ContainsKey("expired-key")); // Expired
            Assert.False(expirations.ContainsKey("no-expiration-key")); // No expiration
            Assert.False(expirations.ContainsKey("nonexistent-key")); // Doesn't exist

            Assert.True(expirations.TryGetValue("valid-key", out var validKeyExpiration));
            Assert.NotNull(validKeyExpiration);
            Assert.True(validKeyExpiration.Value > TimeSpan.FromMinutes(9));
            Assert.True(validKeyExpiration.Value <= TimeSpan.FromMinutes(10));
        }
    }

    public virtual async Task GetAllExpirationAsync_WithLargeNumberOfKeys_ReturnsAllExpirations(int count)
    {
        // Arrange
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var keys = new List<string>();
            for (int i = 0; i < count; i++)
            {
                string key = $"perf-test-key-{i}";
                keys.Add(key);
                await cache.SetAsync(key, i, TimeSpan.FromMinutes(i % 60 + 1));
            }

            // Act
            var sw = Stopwatch.StartNew();
            var expirations = await cache.GetAllExpirationAsync(keys);
            sw.Stop();

            _logger.LogInformation("Get All Expiration Time ({Count} keys): {Elapsed:g}", count, sw.Elapsed);

            // Assert
            Assert.Equal(count, expirations.Count);
            Assert.All(expirations, kvp => Assert.NotNull(kvp.Value));
        }
    }

    public virtual async Task GetAllExpirationAsync_WithInvalidKeys_ValidatesCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            // Null keys collection throws ArgumentNullException
            await Assert.ThrowsAsync<ArgumentNullException>(async () => await cache.GetAllExpirationAsync(null));

            // Keys containing null throws ArgumentNullException
            await Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await cache.GetAllExpirationAsync(["key1", null, "key2"]));

            // Keys containing empty string throws ArgumentException
            await Assert.ThrowsAsync<ArgumentException>(async () =>
                await cache.GetAllExpirationAsync(["key1", "", "key2"]));

            // Empty keys collection returns empty result (not an error)
            var result = await cache.GetAllExpirationAsync([]);
            Assert.NotNull(result);
            Assert.Empty(result);
        }
    }

    public virtual async Task GetAllAsync_WithInvalidKeys_ValidatesCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            // Null keys collection throws ArgumentNullException
            await Assert.ThrowsAsync<ArgumentNullException>(async () => await cache.GetAllAsync<string>(null));

            // Keys containing null throws ArgumentNullException
            await Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await cache.GetAllAsync<string>(["key1", null, "key2"]));

            // Keys containing empty string throws ArgumentException
            await Assert.ThrowsAsync<ArgumentException>(async () =>
                await cache.GetAllAsync<string>(["key1", "", "key2"]));

            // Empty keys collection returns empty result (not an error)
            var result = await cache.GetAllAsync<string>([]);
            Assert.NotNull(result);
            Assert.Empty(result);
        }
    }

    public virtual async Task GetAllAsync_WithMultipleKeys_ReturnsCorrectValues()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            // Test with primitive values including mixed-case, whitespace keys, and null
            await cache.SetAsync("test1", 1);
            await cache.SetAsync("Test1", 2); // Mixed case - different key
            await cache.SetAsync("   ", 3); // Whitespace key
            var intResult = await cache.GetAllAsync<int>(["test1", "Test1", "   ", "nonexistent"]);
            Assert.NotNull(intResult);
            Assert.Equal(4, intResult.Count);
            Assert.Equal(1, intResult["test1"].Value);
            Assert.Equal(2, intResult["Test1"].Value);
            Assert.Equal(3, intResult["   "].Value);
            Assert.False(intResult["nonexistent"].HasValue);

            // Test with complex objects including null values
            await cache.SetAsync("obj1", new SimpleModel { Data1 = "data 1", Data2 = 1 });
            await cache.SetAsync("Obj1", new SimpleModel { Data1 = "data 2", Data2 = 2 });  // Mixed case
            await cache.SetAsync("objNull", (SimpleModel)null);
            var objResult = await cache.GetAllAsync<SimpleModel>(["obj1", "Obj1", "objNull", "objMissing"]);
            Assert.NotNull(objResult);
            Assert.Equal(4, objResult.Count);
            Assert.Equal("data 1", objResult["obj1"].Value.Data1);
            Assert.Equal("data 2", objResult["Obj1"].Value.Data1);
            Assert.True(objResult["objNull"].IsNull);
            Assert.False(objResult["objMissing"].HasValue);
        }
    }

    public virtual async Task GetAllAsync_WithScopedCache_ReturnsUnscopedKeys()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var scopedCache1 = new ScopedCacheClient(cache, "scoped1");
            var nestedScopedCache1 = new ScopedCacheClient(scopedCache1, "nested");

            await scopedCache1.SetAsync("test", 1);
            await nestedScopedCache1.SetAsync("test", 2);

            Assert.Equal("test", (await scopedCache1.GetAllAsync<int>("test")).Keys.FirstOrDefault());
            Assert.Equal("test", (await nestedScopedCache1.GetAllAsync<int>("test")).Keys.FirstOrDefault());
        }
    }


    public virtual async Task GetAsync_WithNumericTypeConversion_ConvertsBetweenTypes()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            // int to long
            await cache.SetAsync("intValue", 1);
            var longResult = await cache.GetAsync<long>("intValue");
            Assert.True(longResult.HasValue);
            Assert.Equal(1L, longResult.Value);

            // long to int
            await cache.SetAsync<long>("longValue", 1);
            var intResult = await cache.GetAsync<int>("longValue");
            Assert.True(intResult.HasValue);
            Assert.Equal(1, intResult.Value);

            // large double
            double largeValue = 2 * 1000 * 1000 * 1000;
            await cache.SetAsync("largeValue", largeValue);
            Assert.Equal(largeValue, await cache.GetAsync<double>("largeValue", 0));

            // overflow throws exception when shouldThrowOnSerializationError is true (default)
            await cache.SetAsync("maxLong", Int64.MaxValue);
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                var cacheValue = await cache.GetAsync<int>("maxLong");
                Assert.False(cacheValue.HasValue);
            });

            var validLongResult = await cache.GetAsync<long>("maxLong");
            Assert.True(validLongResult.HasValue);
            Assert.Equal(Int64.MaxValue, validLongResult.Value);
        }
    }

    public virtual async Task GetAsync_WithTryGetSemantics_HandlesTypeConversions()
    {
        var cache = GetCacheClient(false);
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            // Successful conversions
            await cache.SetAsync("intValue", 1);
            var longResult = await cache.GetAsync<long>("intValue");
            Assert.True(longResult.HasValue);
            Assert.Equal(1L, longResult.Value);

            await cache.SetAsync<long>("longValue", 1);
            var intResult = await cache.GetAsync<int>("longValue");
            Assert.True(intResult.HasValue);
            Assert.Equal(1, intResult.Value);

            // Overflow returns no value instead of throwing
            await cache.SetAsync("maxLong", Int64.MaxValue);
            var overflowResult = await cache.GetAsync<int>("maxLong");
            Assert.False(overflowResult.HasValue);

            var validLongResult = await cache.GetAsync<long>("maxLong");
            Assert.True(validLongResult.HasValue);
            Assert.Equal(Int64.MaxValue, validLongResult.Value);

            // Complex type as primitive returns no value
            await cache.SetAsync("complex", new MyData { Message = "test" });
            var complexAsLong = await cache.GetAsync<long>("complex");
            Assert.False(complexAsLong.HasValue);
        }
    }

    public virtual async Task GetAsync_WithComplexObject_PreservesPropertiesAndReturnsNewInstance()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            // Non-existent key returns no value
            Assert.False((await cache.GetAsync<MyData>("non-existent-key")).HasValue);
            Assert.False(await cache.ExistsAsync("non-existent-key"));

            var dt = DateTimeOffset.Now;
            var value = new MyData { Type = "test", Date = dt, Message = "Hello World" };

            await cache.SetAsync("order:details", value);
            value.Type = "modified";

            var cachedValue = await cache.GetAsync<MyData>("order:details");
            Assert.NotNull(cachedValue);
            Assert.False(value.Equals(cachedValue.Value), "Should not be same reference object");
            Assert.Equal("test", cachedValue.Value.Type);
            Assert.NotEqual("modified", cachedValue.Value.Type);
            Assert.Equal(dt, cachedValue.Value.Date);
            Assert.Equal("Hello World", cachedValue.Value.Message);

            // Verify case sensitivity - different cased keys should be treated as distinct
            await cache.SetAsync("userId", new MyData { Type = "lowercase", Date = dt, Message = "Lower" });
            await cache.SetAsync("USERID", new MyData { Type = "uppercase", Date = dt, Message = "Upper" });

            var lowerResult = await cache.GetAsync<MyData>("userId");
            var upperResult = await cache.GetAsync<MyData>("USERID");

            Assert.True(lowerResult.HasValue);
            Assert.True(upperResult.HasValue);
            Assert.Equal("lowercase", lowerResult.Value.Type);
            Assert.Equal("uppercase", upperResult.Value.Type);
        }
    }

    public virtual async Task GetAsync_WithInvalidKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () => await cache.GetAsync<string>(null));
            await Assert.ThrowsAsync<ArgumentException>(async () => await cache.GetAsync<string>(String.Empty));
        }
    }

    public virtual async Task GetExpirationAsync_WithVariousKeyStates_ReturnsExpectedExpiration()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            const string cacheKey = "test-expiration-key";

            // Non-existent key returns null
            var expiration = await cache.GetExpirationAsync(cacheKey);
            Assert.Null(expiration);

            // Key without expiration returns null
            await cache.SetAsync(cacheKey, "value");
            expiration = await cache.GetExpirationAsync(cacheKey);
            Assert.Null(expiration);

            // Key with expiration returns correct TimeSpan
            await cache.SetAsync(cacheKey, "value", DateTime.UtcNow.AddHours(1));
            expiration = await cache.GetExpirationAsync(cacheKey);
            Assert.NotNull(expiration);
            Assert.InRange(expiration.Value, TimeSpan.FromMinutes(59),
                TimeSpan.FromHours(1).Add(TimeSpan.FromSeconds(10)));

            // Expired key returns null
            await cache.RemoveAsync(cacheKey);
            await cache.SetAsync(cacheKey, 1, DateTime.UtcNow.AddMilliseconds(50));

            await Task.Delay(100);

            expiration = await cache.GetExpirationAsync(cacheKey);
            Assert.Null(expiration);
        }
    }

    public virtual async Task GetExpirationAsync_WithInvalidKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () => await cache.GetExpirationAsync(null));
            await Assert.ThrowsAsync<ArgumentException>(async () => await cache.GetExpirationAsync(String.Empty));
        }
    }

    public virtual async Task GetListAsync_WithInvalidKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "list:validation";

            await Assert.ThrowsAsync<ArgumentNullException>(() => cache.GetListAsync<int>(null!));
            await Assert.ThrowsAsync<ArgumentException>(() => cache.GetListAsync<int>(String.Empty));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => cache.GetListAsync<int>(key, 0, 5));
        }
    }

    public virtual async Task GetListAsync_WithPaging_ReturnsCorrectResults()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "list:paging";

            int[] values = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20];
            await cache.ListAddAsync(key, values, TimeSpan.FromMinutes(1));

            // Verify first page returns correct page size
            var firstPage = await cache.GetListAsync<int>(key, 1, 5);
            Assert.NotNull(firstPage);
            Assert.Equal(5, firstPage.Value.Count);
            var firstPageItems = new HashSet<int>(firstPage.Value);

            // Verify all items can be retrieved across multiple pages
            var allItems = new HashSet<int>(values.Length);
            for (int page = 1; page <= values.Length / 5; page++)
            {
                var pagedResult = await cache.GetListAsync<int>(key, page, 5);
                Assert.NotNull(pagedResult);
                Assert.Equal(5, pagedResult.Value.Count);
                allItems.AddRange(pagedResult.Value);
            }
            Assert.Equal(values.Length, allItems.Count);

            // Verify page beyond end returns empty collection
            var beyondEnd = await cache.GetListAsync<int>(key, 10, 5);
            Assert.NotNull(beyondEnd);
            Assert.Empty(beyondEnd.Value);

            // Verify new items are added at the end and first page remains stable
            await cache.ListAddAsync(key, [21, 22], TimeSpan.FromMinutes(1));
            var lastPageResult = await cache.GetListAsync<int>(key, 5, 5);
            Assert.NotNull(lastPageResult);
            Assert.Equal(2, lastPageResult.Value.Count);

            var firstPageAgain = await cache.GetListAsync<int>(key, 1, 5);
            Assert.Equal(firstPageItems, firstPageAgain.Value.ToHashSet());
        }
    }

    public virtual async Task GetListAsync_WithExpiredItems_RemovesExpiredAndReturnsActive()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "list:expiration";

            Assert.Equal(1, await cache.ListAddAsync(key, [1], TimeSpan.FromMilliseconds(100)));

            var cacheValue = await cache.GetListAsync<int>(key);
            Assert.True(cacheValue.HasValue);
            Assert.Single(cacheValue.Value);
            Assert.True(cacheValue.Value.Contains(1));

            await Task.Delay(150);

            // GetList should invalidate expired items
            cacheValue = await cache.GetListAsync<int>(key);
            Assert.False(cacheValue.HasValue);
            Assert.False(await cache.ExistsAsync(key));
        }
    }

    public virtual async Task IncrementAsync_WithExpiration_SetsExpirationCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            // Past expiration (long): should return 0 and remove the key
            await cache.SetAsync("increment-past-exp-long", 100L);
            Assert.True(await cache.ExistsAsync("increment-past-exp-long"));
            long longResult = await cache.IncrementAsync("increment-past-exp-long", 5L, TimeSpan.FromMilliseconds(-1));
            Assert.Equal(0, longResult);
            Assert.False(await cache.ExistsAsync("increment-past-exp-long"));
            Assert.False((await cache.GetAsync<long>("increment-past-exp-long")).HasValue);

            // Past expiration (double): should return 0 and remove the key
            await cache.SetAsync("increment-past-exp-double", 100.5);
            Assert.True(await cache.ExistsAsync("increment-past-exp-double"));
            double doubleResult = await cache.IncrementAsync("increment-past-exp-double", 5.5, TimeSpan.FromMilliseconds(-1));
            Assert.Equal(0, doubleResult);
            Assert.False(await cache.ExistsAsync("increment-past-exp-double"));
            Assert.False((await cache.GetAsync<double>("increment-past-exp-double")).HasValue);

            // Past expiration on non-existent key: should return 0
            longResult = await cache.IncrementAsync("increment-past-exp-nonexistent", 5L, TimeSpan.FromMilliseconds(-1));
            Assert.Equal(0, longResult);
            Assert.False(await cache.ExistsAsync("increment-past-exp-nonexistent"));

            // Zero expiration: should also be treated as expired
            await cache.SetAsync("increment-zero-exp", 100L);
            Assert.True(await cache.ExistsAsync("increment-zero-exp"));
            longResult = await cache.IncrementAsync("increment-zero-exp", 5L, TimeSpan.Zero);
            Assert.Equal(0, longResult);
            Assert.False(await cache.ExistsAsync("increment-zero-exp"));
            Assert.False((await cache.GetAsync<long>("increment-zero-exp")).HasValue);

            // Max expiration (long): should succeed and key should exist with no expiration
            longResult = await cache.IncrementAsync("increment-max-exp-long", 100L, TimeSpan.MaxValue);
            Assert.Equal(100, longResult);
            Assert.True(await cache.ExistsAsync("increment-max-exp-long"));
            Assert.Equal(100L, (await cache.GetAsync<long>("increment-max-exp-long")).Value);
            var expiration = await cache.GetExpirationAsync("increment-max-exp-long");
            Assert.Null(expiration); // TimeSpan.MaxValue means no expiration

            // Max expiration (double): should succeed and key should exist with no expiration
            doubleResult = await cache.IncrementAsync("increment-max-exp-double", 100.5, TimeSpan.MaxValue);
            Assert.Equal(100.5, doubleResult);
            Assert.True(await cache.ExistsAsync("increment-max-exp-double"));
            Assert.Equal(100.5, (await cache.GetAsync<double>("increment-max-exp-double")).Value);
            expiration = await cache.GetExpirationAsync("increment-max-exp-double");
            Assert.Null(expiration); // TimeSpan.MaxValue means no expiration

            // Normal expiration (long): should succeed and key should exist with correct expiration
            longResult = await cache.IncrementAsync("increment-normal-exp-long", 100L, TimeSpan.FromHours(1));
            Assert.Equal(100, longResult);
            Assert.True(await cache.ExistsAsync("increment-normal-exp-long"));
            Assert.Equal(100L, (await cache.GetAsync<long>("increment-normal-exp-long")).Value);
            expiration = await cache.GetExpirationAsync("increment-normal-exp-long");
            Assert.NotNull(expiration);
            Assert.InRange(expiration.Value, TimeSpan.FromMinutes(59), TimeSpan.FromHours(1));

            // Normal expiration (double): should succeed and key should exist with correct expiration
            doubleResult = await cache.IncrementAsync("increment-normal-exp-double", 100.5, TimeSpan.FromHours(1));
            Assert.Equal(100.5, doubleResult);
            Assert.True(await cache.ExistsAsync("increment-normal-exp-double"));
            Assert.Equal(100.5, (await cache.GetAsync<double>("increment-normal-exp-double")).Value);
            expiration = await cache.GetExpirationAsync("increment-normal-exp-double");
            Assert.NotNull(expiration);
            Assert.InRange(expiration.Value, TimeSpan.FromMinutes(59), TimeSpan.FromHours(1));

            // Normal expiration actually expires after delay
            Assert.True(await cache.SetAsync("increment-expiration-test", 0));
            double newVal = await cache.IncrementAsync("increment-expiration-test", 1, TimeSpan.FromMilliseconds(50));
            Assert.Equal(1, newVal);
            await Task.Delay(100);
            Assert.False((await cache.GetAsync<int>("increment-expiration-test")).HasValue);

            // Null expiration (long): calling with null should preserve existing expiration
            longResult = await cache.IncrementAsync("increment-null-exp-long", 100L, TimeSpan.FromHours(1));
            Assert.Equal(100, longResult);
            Assert.True(await cache.ExistsAsync("increment-null-exp-long"));
            expiration = await cache.GetExpirationAsync("increment-null-exp-long");
            Assert.NotNull(expiration);

            // Now call without expiration - should succeed and preserve existing expiration
            longResult = await cache.IncrementAsync("increment-null-exp-long", 5L);
            Assert.Equal(105, longResult);
            Assert.True(await cache.ExistsAsync("increment-null-exp-long"));
            expiration = await cache.GetExpirationAsync("increment-null-exp-long");
            Assert.NotNull(expiration); // Expiration is preserved

            // Null expiration (double): calling with null should preserve existing expiration
            doubleResult = await cache.IncrementAsync("increment-null-exp-double", 100.5, TimeSpan.FromHours(1));
            Assert.Equal(100.5, doubleResult);
            Assert.True(await cache.ExistsAsync("increment-null-exp-double"));
            expiration = await cache.GetExpirationAsync("increment-null-exp-double");
            Assert.NotNull(expiration);

            // Now call without expiration - should succeed and preserve existing expiration
            doubleResult = await cache.IncrementAsync("increment-null-exp-double", 5.5);
            Assert.Equal(106, doubleResult);
            Assert.True(await cache.ExistsAsync("increment-null-exp-double"));
            expiration = await cache.GetExpirationAsync("increment-null-exp-double");
            Assert.NotNull(expiration); // Expiration is preserved
        }
    }

    public virtual async Task IncrementAsync_WithInvalidKey_ThrowsException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () => await cache.IncrementAsync(null, 1));
            await Assert.ThrowsAsync<ArgumentException>(async () => await cache.IncrementAsync(String.Empty, 1));
        }
    }

    public virtual async Task IncrementAsync_WithKey_IncrementsCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            // Non-existent key with default amount initializes to 1 (also tests case-sensitivity)
            Assert.Equal(1, await cache.IncrementAsync("counter"));
            Assert.Equal(5, await cache.IncrementAsync("Counter", 5));
            Assert.Equal(0, await cache.IncrementAsync("COUNTER", 0));

            // Increment existing key
            Assert.Equal(2, await cache.IncrementAsync("counter"));

            // Verify all three case-sensitive keys have correct values
            Assert.Equal(2, (await cache.GetAsync<long>("counter")).Value);
            Assert.Equal(5, (await cache.GetAsync<long>("Counter")).Value);
            Assert.Equal(0, (await cache.GetAsync<long>("COUNTER")).Value);
        }
    }

    public virtual async Task IncrementAsync_WithScopedCache_WorksWithinScope()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var scopedCache1 = new ScopedCacheClient(cache, "scoped1");
            var nestedScopedCache1 = new ScopedCacheClient(scopedCache1, "nested");

            Assert.Equal(0, await scopedCache1.GetAsync<double>("total", 0));
            Assert.Equal(10, await scopedCache1.IncrementAsync("total", 10));
            Assert.Equal(10, await scopedCache1.GetAsync<double>("total", 0));

            Assert.Equal(0, await nestedScopedCache1.GetAsync<double>("total", 0));
            Assert.Equal(20, await nestedScopedCache1.IncrementAsync("total", 20));
            Assert.Equal(20, await nestedScopedCache1.GetAsync<double>("total", 0));
            Assert.Equal(1, await nestedScopedCache1.RemoveAllAsync(["id", "total"]));
            Assert.Equal(0, await nestedScopedCache1.GetAsync<double>("total", 0));

            Assert.Equal(1, await scopedCache1.RemoveAllAsync(["id", "total"]));
            Assert.Equal(0, await scopedCache1.GetAsync<double>("total", 0));
        }
    }

    public virtual async Task IncrementAsync_WithFloatingPointDecimals_PreservesDecimalPrecision()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            // Test increment with fractional values
            double result = await cache.IncrementAsync("decimal-counter", 1.5);
            Assert.Equal(1.5, result);
            Assert.Equal(1.5, (await cache.GetAsync<double>("decimal-counter")).Value);

            // Increment with another fractional value
            result = await cache.IncrementAsync("decimal-counter", 2.25);
            Assert.Equal(3.75, result);
            Assert.Equal(3.75, (await cache.GetAsync<double>("decimal-counter")).Value);

            // Decrement with fractional value (negative increment)
            result = await cache.IncrementAsync("decimal-counter", -0.75);
            Assert.Equal(3.0, result);
            Assert.Equal(3.0, (await cache.GetAsync<double>("decimal-counter")).Value);

            // Test with very small fractional values
            result = await cache.IncrementAsync("small-decimal", 0.001);
            Assert.Equal(0.001, result);

            result = await cache.IncrementAsync("small-decimal", 0.002);
            Assert.Equal(0.003, result);

            // Verify the values with tolerance for floating-point precision
            var storedValue = (await cache.GetAsync<double>("small-decimal")).Value;
            Assert.True(Math.Abs(storedValue - 0.003) < 0.0001, $"Expected ~0.003 but got {storedValue}");
        }
    }

    public virtual async Task SetIfHigherAsync_WithFloatingPointDecimals_ComparesCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            // Initialize with a decimal value
            double result = await cache.SetIfHigherAsync("decimal-high", 10.5);
            Assert.Equal(10.5, result);
            Assert.Equal(10.5, (await cache.GetAsync<double>("decimal-high")).Value);

            // Set higher with a value that differs only in decimals
            result = await cache.SetIfHigherAsync("decimal-high", 10.75);
            Assert.Equal(0.25, result); // difference should be 0.25
            Assert.Equal(10.75, (await cache.GetAsync<double>("decimal-high")).Value);

            // Try to set lower - should not update
            result = await cache.SetIfHigherAsync("decimal-high", 10.5);
            Assert.Equal(0, result);
            Assert.Equal(10.75, (await cache.GetAsync<double>("decimal-high")).Value);

            // Try to set with same value - should not update
            result = await cache.SetIfHigherAsync("decimal-high", 10.75);
            Assert.Equal(0, result);
            Assert.Equal(10.75, (await cache.GetAsync<double>("decimal-high")).Value);

            // Set higher with larger fractional part
            result = await cache.SetIfHigherAsync("decimal-high", 11.125);
            Assert.Equal(0.375, result); // difference should be 0.375
            Assert.Equal(11.125, (await cache.GetAsync<double>("decimal-high")).Value);
        }
    }

    public virtual async Task SetIfLowerAsync_WithFloatingPointDecimals_ComparesCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            // Initialize with a decimal value
            double result = await cache.SetIfLowerAsync("decimal-low", 100.75);
            Assert.Equal(100.75, result);
            Assert.Equal(100.75, (await cache.GetAsync<double>("decimal-low")).Value);

            // Set lower with a value that differs only in decimals
            result = await cache.SetIfLowerAsync("decimal-low", 100.25);
            Assert.Equal(0.5, result); // difference should be 0.5
            Assert.Equal(100.25, (await cache.GetAsync<double>("decimal-low")).Value);

            // Try to set higher - should not update
            result = await cache.SetIfLowerAsync("decimal-low", 100.75);
            Assert.Equal(0, result);
            Assert.Equal(100.25, (await cache.GetAsync<double>("decimal-low")).Value);

            // Try to set with same value - should not update
            result = await cache.SetIfLowerAsync("decimal-low", 100.25);
            Assert.Equal(0, result);
            Assert.Equal(100.25, (await cache.GetAsync<double>("decimal-low")).Value);

            // Set lower with smaller fractional part
            result = await cache.SetIfLowerAsync("decimal-low", 99.875);
            Assert.Equal(0.375, result); // difference should be 0.375
            Assert.Equal(99.875, (await cache.GetAsync<double>("decimal-low")).Value);
        }
    }

    public virtual async Task ListAddAsync_WithExpiration_SetsExpirationCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            // Past expiration on new key: should return 0 and key should not exist
            long result = await cache.ListAddAsync("list-past-exp-new", [1], TimeSpan.FromMilliseconds(-1));
            Assert.Equal(0, result);
            Assert.False(await cache.ExistsAsync("list-past-exp-new"));
            Assert.False((await cache.GetListAsync<int>("list-past-exp-new")).HasValue);

            // Past expiration on existing key: should return 0 and remove the key
            Assert.Equal(1, await cache.ListAddAsync("list-past-exp-existing", [1]));
            Assert.True(await cache.ExistsAsync("list-past-exp-existing"));
            result = await cache.ListAddAsync("list-past-exp-existing", [2], TimeSpan.FromSeconds(-1));
            Assert.Equal(0, result);
            Assert.False(await cache.ExistsAsync("list-past-exp-existing"));
            Assert.False((await cache.GetListAsync<int>("list-past-exp-existing")).HasValue);

            // Zero expiration: should also be treated as expired
            result = await cache.ListAddAsync("list-zero-exp", [1], TimeSpan.Zero);
            Assert.Equal(0, result);
            Assert.False(await cache.ExistsAsync("list-zero-exp"));
            Assert.False((await cache.GetListAsync<int>("list-zero-exp")).HasValue);

            // Max expiration: should succeed and key should exist with no expiration
            result = await cache.ListAddAsync("list-max-exp", [1, 2, 3], TimeSpan.MaxValue);
            Assert.Equal(3, result);
            Assert.True(await cache.ExistsAsync("list-max-exp"));
            var listValue = await cache.GetListAsync<int>("list-max-exp");
            Assert.True(listValue.HasValue);
            Assert.Equal(3, listValue.Value.Count);
            var expiration = await cache.GetExpirationAsync("list-max-exp");
            Assert.Null(expiration); // TimeSpan.MaxValue means no expiration

            // Normal expiration: should succeed and key should exist with correct expiration
            result = await cache.ListAddAsync("list-normal-exp", [1, 2, 3], TimeSpan.FromHours(1));
            Assert.Equal(3, result);
            Assert.True(await cache.ExistsAsync("list-normal-exp"));
            listValue = await cache.GetListAsync<int>("list-normal-exp");
            Assert.True(listValue.HasValue);
            Assert.Equal(3, listValue.Value.Count);
            expiration = await cache.GetExpirationAsync("list-normal-exp");
            Assert.NotNull(expiration);
            Assert.InRange(expiration.Value, TimeSpan.FromMinutes(59), TimeSpan.FromHours(1));

            // Multiple expirations expire individual items - test staggered expiration in one pass
            const string key = "list:staggered-expiration";
            Assert.Equal(1, await cache.ListAddAsync(key, [2], TimeSpan.FromMilliseconds(50)));
            Assert.Equal(1, await cache.ListAddAsync(key, [3], TimeSpan.FromMilliseconds(150)));

            var cacheValue = await cache.GetListAsync<int>(key);
            Assert.True(cacheValue.HasValue);
            Assert.Equal(2, cacheValue.Value.Count);

            // Wait for first item to expire
            await Task.Delay(75);
            cacheValue = await cache.GetListAsync<int>(key);
            Assert.True(cacheValue.HasValue);
            Assert.Single(cacheValue.Value);
            Assert.Contains(3, cacheValue.Value);

            // Wait for second item to expire
            await Task.Delay(100);
            Assert.False(await cache.ExistsAsync(key));

            // Null expiration: should succeed and remove expiration
            result = await cache.ListAddAsync("list-null-exp", [1, 2], TimeSpan.FromHours(1));
            Assert.Equal(2, result);
            Assert.True(await cache.ExistsAsync("list-null-exp"));
            expiration = await cache.GetExpirationAsync("list-null-exp");
            Assert.NotNull(expiration);

            // Now add without expiration - should succeed and remove expiration
            result = await cache.ListAddAsync("list-null-exp", [3]);
            Assert.Equal(1, result);
            Assert.True(await cache.ExistsAsync("list-null-exp"));
            listValue = await cache.GetListAsync<int>("list-null-exp");
            Assert.True(listValue.HasValue);
            Assert.Equal(3, listValue.Value.Count);
            expiration = await cache.GetExpirationAsync("list-null-exp");
            Assert.Null(expiration);
        }
    }

    public virtual async Task ListAddAsync_WithInvalidArguments_ThrowsException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            // Null key
            await Assert.ThrowsAsync<ArgumentNullException>(() => cache.ListAddAsync(null, 1));

            // Empty key
            await Assert.ThrowsAsync<ArgumentException>(() => cache.ListAddAsync(String.Empty, "value"));

            // Null collection
            await Assert.ThrowsAsync<ArgumentNullException>(() => cache.ListAddAsync("list:validation", null as List<int>));

            // Existing non-list key
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                await cache.AddAsync("list:non-list-key", 1);
                await cache.ListAddAsync("list:non-list-key", 1);
            });
        }
    }

    public virtual async Task ListAddAsync_WithSingleString_StoresAsStringNotCharArray()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "list:string";

            await cache.ListAddAsync(key, "my-value");
            var stringResult = await cache.GetListAsync<string>(key);
            Assert.Single(stringResult.Value);
            Assert.Equal("my-value", stringResult.Value.First());

            await cache.ListRemoveAsync(key, "my-value");
            stringResult = await cache.GetListAsync<string>(key);
            Assert.Empty(stringResult.Value);
        }
    }

    public virtual async Task ListAddAsync_WithVariousInputs_HandlesCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "list:inputs";

            // Duplicates are stored as unique values only
            Assert.Equal(3, await cache.ListAddAsync(key, new List<int> { 1, 1, 2, 3 }));
            var result = await cache.GetListAsync<int>(key);
            Assert.NotNull(result);
            Assert.Equal(3, result.Value.Count);

            Assert.True(await cache.ListRemoveAsync(key, 1));
            result = await cache.GetListAsync<int>(key);
            Assert.NotNull(result);
            Assert.Equal(2, result.Value.Count);

            await cache.RemoveAllAsync();

            // Empty collection is no-op
            await cache.ListAddAsync<int>(key, []);
            await cache.ListAddAsync(key, 1);
            await cache.ListAddAsync(key, 2);
            await cache.ListAddAsync(key, 3);
            result = await cache.GetListAsync<int>(key);
            Assert.NotNull(result);
            Assert.Equal(3, result.Value.Count);

            await cache.RemoveAllAsync();

            // Null items are ignored
            Assert.Equal(0, await cache.ListAddAsync<string>(key, [null]));
            Assert.Equal(1, await cache.ListAddAsync(key, ["1", null]));
            var stringResult = await cache.GetListAsync<string>(key);
            Assert.NotNull(stringResult);
            Assert.Single(stringResult.Value);
        }
    }

    public virtual async Task ListRemoveAsync_WithInvalidInputs_ThrowsAppropriateException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            // Null key throws ArgumentNullException
            await Assert.ThrowsAsync<ArgumentNullException>(() => cache.ListRemoveAsync(null, 1));

            // Empty key throws ArgumentException
            await Assert.ThrowsAsync<ArgumentException>(() => cache.ListRemoveAsync(String.Empty, "value"));

            // Null collection throws ArgumentNullException
            await Assert.ThrowsAsync<ArgumentNullException>(() => cache.ListRemoveAsync("list:remove:test", null as List<int>));
        }
    }

    public virtual async Task ListRemoveAsync_WithValues_RemovesCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "list:remove:values";

            // Remove multiple values at once
            await cache.ListAddAsync(key, [1, 2, 3, 3]);
            await cache.ListRemoveAsync(key, [1, 3]);
            var result = await cache.GetListAsync<int>(key);
            Assert.NotNull(result);
            Assert.Single(result.Value);
            Assert.Contains(2, result.Value);

            // Remove remaining value
            await cache.ListRemoveAsync(key, [2]);
            result = await cache.GetListAsync<int>(key);
            Assert.NotNull(result);
            Assert.Empty(result.Value);

            // Null items in collection are ignored - use different key to avoid type conflict
            const string nullItemsKey = "list:remove:nullitems";
            await cache.ListAddAsync(nullItemsKey, ["1"]);
            Assert.Equal(0, await cache.ListRemoveAsync<string>(nullItemsKey, [null]));
            Assert.Equal(1, await cache.ListRemoveAsync(nullItemsKey, ["1", null]));
        }
    }

    public virtual async Task ListRemoveAsync_WithValidValues_RemovesKeyWhenEmpty()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "list:remove:cleanup";

            Assert.Equal(2, await cache.ListAddAsync(key, [1, 2]));

            Assert.Equal(1, await cache.ListRemoveAsync(key, [1], TimeSpan.FromSeconds(-1)));
            Assert.Equal(0, await cache.ListRemoveAsync(key, [1], TimeSpan.FromSeconds(-1)));

            var cacheValue = await cache.GetListAsync<int>(key);
            Assert.True(cacheValue.HasValue);
            Assert.Single(cacheValue.Value);
            Assert.True(cacheValue.Value.Contains(2));

            // Expiration is not taken into account since it's a remove operation.
            Assert.Equal(1, await cache.ListRemoveAsync(key, [2], TimeSpan.FromSeconds(1)));
            Assert.False(await cache.ExistsAsync(key));
        }
    }

    public virtual async Task ListRemoveAsync_WithExpiration_SetsExpirationCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            // NOTE: The expiresIn parameter on ListRemoveAsync is currently NOT implemented
            // in InMemoryCacheClient - it is a no-op. This test documents the expected behavior
            // that SHOULD be implemented: expiration should be applied to remaining items after removal.
            // For now, this test verifies the remove operation works, but expiration is ignored.

            // Setup: add items to list
            const string key = "list-remove-exp";
            Assert.Equal(3, await cache.ListAddAsync(key, [1, 2, 3]));
            Assert.True(await cache.ExistsAsync(key));

            // Past expiration: currently the expiresIn is ignored, so remove still works
            // Expected behavior (not implemented): past expiration should remove all remaining items
            long removed = await cache.ListRemoveAsync(key, [1], TimeSpan.FromMilliseconds(-1));
            Assert.Equal(1, removed);
            // Key still exists because expiresIn is not implemented
            Assert.True(await cache.ExistsAsync(key));
            var listValue = await cache.GetListAsync<int>(key);
            Assert.True(listValue.HasValue);
            Assert.Equal(2, listValue.Value.Count);

            // Normal expiration: currently ignored
            removed = await cache.ListRemoveAsync(key, [2], TimeSpan.FromHours(1));
            Assert.Equal(1, removed);
            Assert.True(await cache.ExistsAsync(key));
            listValue = await cache.GetListAsync<int>(key);
            Assert.True(listValue.HasValue);
            Assert.Single(listValue.Value);
            Assert.Contains(3, listValue.Value);

            // Max expiration: currently ignored
            removed = await cache.ListRemoveAsync(key, [3], TimeSpan.MaxValue);
            Assert.Equal(1, removed);
            // After removing all items, key should not exist
            Assert.False(await cache.ExistsAsync(key));
        }
    }

    public virtual async Task RemoveAllAsync_WithScopedCache_AffectsOnlyScopedKeys()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var scopedCache1 = new ScopedCacheClient(cache, "scoped1");
            var scopedCache2 = new ScopedCacheClient(cache, "scoped2");

            await cache.SetAsync("test", 1);
            await scopedCache1.SetAsync("test", 2);
            await scopedCache2.SetAsync("test", 3);

            await scopedCache2.RemoveAllAsync();
            Assert.Equal(2, (await scopedCache1.GetAsync<int>("test")).Value);
            Assert.False((await scopedCache2.GetAsync<int>("test")).HasValue);
            Assert.Equal(1, (await cache.GetAsync<int>("test")).Value);
        }
    }

    public virtual async Task RemoveAllAsync_WithLargeNumberOfKeys_RemovesAllKeysEfficiently()
    {
        const int COUNT = 10000;

        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            Assert.Equal(0, await cache.RemoveAllAsync());

            var dictionary = Enumerable.Range(0, COUNT).ToDictionary(i => $"remove-all:{i}");

            var sw = Stopwatch.StartNew();
            await cache.SetAllAsync(dictionary);
            sw.Stop();
            _logger.LogInformation("Set All Time: {Elapsed:g}", sw.Elapsed);

            sw = Stopwatch.StartNew();
            Assert.Equal(COUNT, await cache.RemoveAllAsync());
            sw.Stop();
            _logger.LogInformation("Remove All Time: {Elapsed:g}", sw.Elapsed);

            Assert.False(await cache.ExistsAsync("remove-all:0"));
            Assert.False(await cache.ExistsAsync($"remove-all:{COUNT - 1}"));
        }
    }

    public virtual async Task RemoveAllAsync_WithSpecificKeyCollection_RemovesOnlySpecifiedKeys()
    {
        const int COUNT = 10000;

        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var dictionary = Enumerable.Range(0, COUNT).ToDictionary(i => $"remove-all-keys:{i}");

            var sw = Stopwatch.StartNew();
            await cache.SetAllAsync(dictionary);
            sw.Stop();
            _logger.LogInformation("Set All Time: {Elapsed:g}", sw.Elapsed);

            sw = Stopwatch.StartNew();
            Assert.Equal(COUNT, await cache.RemoveAllAsync(dictionary.Keys));
            sw.Stop();
            _logger.LogInformation("Remove All Time: {Elapsed:g}", sw.Elapsed);

            Assert.False(await cache.ExistsAsync("remove-all-keys:0"));
            Assert.False(await cache.ExistsAsync($"remove-all-keys:{COUNT - 1}"));

            // Verify case sensitivity - only exact matches are removed
            await cache.SetAsync("cacheKey", "val1");
            await cache.SetAsync("CacheKey", "val2");
            await cache.SetAsync("CACHEKEY", "val3");

            await cache.RemoveAllAsync(["CacheKey"]);

            Assert.True((await cache.GetAsync<string>("cacheKey")).HasValue);
            Assert.False((await cache.GetAsync<string>("CacheKey")).HasValue);
            Assert.True((await cache.GetAsync<string>("CACHEKEY")).HasValue);
        }
    }

    public virtual async Task RemoveAllAsync_WithInvalidKeys_ValidatesCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            // Keys containing null throws ArgumentNullException
            await Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await cache.RemoveAllAsync(["key1", null, "key2"]));

            // Keys containing empty string throws ArgumentException
            await Assert.ThrowsAsync<ArgumentException>(async () =>
                await cache.RemoveAllAsync(["key1", String.Empty, "key2"]));

            // Empty keys collection succeeds (no-op)
            await cache.RemoveAllAsync([]);

            // Null keys removes all values
            await cache.SetAsync("key1", 1);
            await cache.SetAsync("key2", 2);
            Assert.True(await cache.ExistsAsync("key1"));
            Assert.True(await cache.ExistsAsync("key2"));

            Assert.Equal(2, await cache.RemoveAllAsync(null));
            Assert.False(await cache.ExistsAsync("key1"));
            Assert.False(await cache.ExistsAsync("key2"));
        }
    }


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

    public virtual async Task RemoveByPrefixAsync_WithScopedCache_AffectsOnlyScopedKeys()
    {
        var cache = GetCacheClient();
        if (cache is null)
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
            await scopedCache2.SetAsync("test", 4);

            int result = await scopedCache1.RemoveByPrefixAsync(String.Empty);
            Assert.Equal(2, result);

            // delete without any matching keys
            result = await scopedCache1.RemoveByPrefixAsync(String.Empty);
            Assert.Equal(0, result);

            Assert.False((await scopedCache1.GetAsync<int>("test")).HasValue);
            Assert.False((await nestedScopedCache1.GetAsync<int>("test")).HasValue);
            Assert.Equal(1, (await cache.GetAsync<int>("test")).Value);
            Assert.Equal(4, (await scopedCache2.GetAsync<int>("test")).Value);
        }
    }

    public virtual async Task RemoveByPrefixAsync_WithMatchingPrefix_RemovesOnlyMatchingKeys()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            const string prefix = "user:";
            await cache.SetAsync("order:123", 1);
            await cache.SetAsync(prefix + "alice", 2);
            await cache.SetAsync(prefix + "bob", 3);
            await cache.SetAsync("User:charlie", 4);
            await cache.SetAsync("USER:dave", 5);

            // Non-matching prefix returns 0
            Assert.Equal(0, await cache.RemoveByPrefixAsync(prefix + "doesntexist"));

            // Matching prefix removes only prefixed keys (case-sensitive)
            Assert.Equal(2, await cache.RemoveByPrefixAsync(prefix));
            Assert.False(await cache.ExistsAsync(prefix + "alice"));
            Assert.False(await cache.ExistsAsync(prefix + "bob"));

            // Unmatched keys remain (including different case prefixes)
            Assert.True(await cache.ExistsAsync("order:123"));
            Assert.True(await cache.ExistsAsync("User:charlie"));
            Assert.True(await cache.ExistsAsync("USER:dave"));
        }
    }

    public virtual async Task RemoveByPrefixAsync_WithNullOrEmptyPrefix_RemovesAllKeys()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            // Test null prefix
            await cache.RemoveAllAsync();
            await cache.SetAsync("user:123", 1);
            await cache.SetAsync("order:456", 2);
            await cache.SetAsync("Product:789", 3);

            int removed = await cache.RemoveByPrefixAsync(null);
            Assert.Equal(3, removed);
            Assert.False(await cache.ExistsAsync("user:123"));
            Assert.False(await cache.ExistsAsync("order:456"));
            Assert.False(await cache.ExistsAsync("Product:789"));

            // Test empty prefix
            await cache.SetAsync("user:123", 1);
            await cache.SetAsync("order:456", 2);
            await cache.SetAsync("Product:789", 3);

            removed = await cache.RemoveByPrefixAsync("");
            Assert.Equal(3, removed);
            Assert.False(await cache.ExistsAsync("user:123"));
            Assert.False(await cache.ExistsAsync("order:456"));
            Assert.False(await cache.ExistsAsync("Product:789"));
        }
    }

    public virtual async Task RemoveByPrefixAsync_WithAsteriskPrefix_TreatedAsLiteral()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            var scopedCache = new ScopedCacheClient(cache, "scoped1");

            const string key = "snowboard";
            await cache.SetAsync(key, 1);
            await scopedCache.SetAsync(key, 1);

            // "*" should be treated as literal, not as wildcard
            Assert.Equal(0, await scopedCache.RemoveByPrefixAsync("*"));
            Assert.True(await cache.ExistsAsync(key));
            Assert.True(await scopedCache.ExistsAsync(key));

            Assert.Equal(0, await cache.RemoveByPrefixAsync("*"));
            Assert.True(await cache.ExistsAsync(key));
            Assert.True(await scopedCache.ExistsAsync(key));

            // "**:" should also be treated as literal prefix
            await cache.SetAsync("**:globMatch", 100);
            await cache.SetAsync("*:singleWildcard", 200);
            await cache.SetAsync("***:tripleAsterisk", 300);

            int removed = await cache.RemoveByPrefixAsync("**:");
            Assert.Equal(1, removed);
            Assert.False(await cache.ExistsAsync("**:globMatch"));
            Assert.True(await cache.ExistsAsync("*:singleWildcard"));
            Assert.True(await cache.ExistsAsync("***:tripleAsterisk"));
        }
    }

    public static IEnumerable<object[]> GetRegexSpecialCharacters()
    {
        return
        [
            ["*"],
            ["+"],
            ["?"],
            ["^"],
            ["$"],
            ["|"],
            ["\\"],
            ["["],
            ["]"],
            ["{"],
            ["}"],
            ["("],
            [")"],
            ["))"], // Invalid regex - extra closing parentheses
            ["(("], // Invalid regex - extra opening parentheses
            ["]]"], // Invalid regex - extra closing brackets
            ["[["], // Invalid regex - extra opening brackets
            ["(()"], // Invalid regex - unbalanced parentheses
            ["([)]"], // Invalid regex - incorrectly nested
            ["[{}]"], // Invalid regex - brackets with braces inside
            ["{{}"], // Invalid regex - unbalanced braces
            ["+++"], // Invalid regex - multiple plus operators
            ["***"], // Invalid regex - multiple asterisks
            ["???"] // Invalid regex - multiple question marks
        ];
    }

    public virtual async Task RemoveByPrefixAsync_WithRegexMetacharacter_TreatsAsLiteral(string specialChar)
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            string regexPrefix = $"pattern{specialChar}:";
            await cache.SetAsync($"{regexPrefix}searchResult", 100);
            await cache.SetAsync($"{regexPrefix}matchResult", 200);
            await cache.SetAsync($"unrelated{specialChar}data", 300);

            int removed = await cache.RemoveByPrefixAsync(regexPrefix);
            Assert.Equal(2, removed);
            Assert.False(await cache.ExistsAsync($"{regexPrefix}searchResult"));
            Assert.False(await cache.ExistsAsync($"{regexPrefix}matchResult"));
            Assert.True(await cache.ExistsAsync($"unrelated{specialChar}data"));
        }
    }

    public static IEnumerable<object[]> GetWildcardPatterns()
    {
        return
        [
            ["**:"],
            ["*.*"],
            ["*.*:"],
            ["*.txt:"],
            ["**/**:"],
            ["glob*.*:"],
            ["pattern**suffix:"]
        ];
    }

    public virtual async Task RemoveByPrefixAsync_WithWildcardPattern_TreatsAsLiteral(string pattern)
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            await cache.SetAsync($"{pattern}fileA", 1000);
            await cache.SetAsync($"{pattern}fileB", 2000);
            await cache.SetAsync($"different{pattern}item", 3000);
            await cache.SetAsync($"excluded{pattern.Replace("*", "X")}item", 4000);

            int removed = await cache.RemoveByPrefixAsync(pattern);
            Assert.Equal(2, removed);
            Assert.False(await cache.ExistsAsync($"{pattern}fileA"));
            Assert.False(await cache.ExistsAsync($"{pattern}fileB"));
            Assert.True(await cache.ExistsAsync($"different{pattern}item"));
            Assert.True(await cache.ExistsAsync($"excluded{pattern.Replace("*", "X")}item"));
        }
    }

    public static IEnumerable<object[]> GetSpecialPrefixes()
    {
        return
        [
            ["space test:"],
            ["tab\t:"],
            ["newline\n:"],
            ["unicode_测试:"],
            ["emoji_🔥:"],
            ["double::colon:"],
            ["dots...:"],
            ["dashes---:"],
            ["underscores___:"],
            ["mixed_sp3c!@l#:"],
            ["percent%encoded:"],
            ["json{\"key\":\"value\"}:"],
            ["xml<tag>:</tag>"],
            ["url://protocol:"],
            ["query?param=value:"],
            ["fragment#anchor:"],
            ["ampersand&and:"],
            ["equals=sign:"],
            ["semicolon;sep:"],
            ["comma,sep:"],
            ["quotes\"single':"],
            ["backtick`:"],
            ["tilde~:"],
            ["exclamation!:"],
            ["at@symbol:"],
            ["hash#tag:"],
            ["dollar$sign:"],
            ["caret^symbol:"],
            ["ampersand&symbol:"],
            ["asterisk*symbol:"],
            ["parentheses():"],
            ["minus-dash:"],
            ["plus+sign:"],
            ["equals=symbol:"],
            ["brackets[]:"],
            ["braces{}:"],
            ["backslash\\:"],
            ["pipe|symbol:"],
            ["less<than:"],
            ["greater>than:"],
            ["question?mark:"],
            ["forwardslash/:"],
            ["period.dot:"]
        ];
    }

    public virtual async Task RemoveByPrefixAsync_WithSpecialCharacterPrefix_TreatsAsLiteral(string specialPrefix)
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            await cache.SetAsync($"{specialPrefix}encodedValue", 100);
            await cache.SetAsync($"{specialPrefix}escapedString", 200);
            await cache.SetAsync($"unmatched{specialPrefix}entry", 300);

            int removed = await cache.RemoveByPrefixAsync(specialPrefix);
            Assert.Equal(2, removed);
            Assert.False(await cache.ExistsAsync($"{specialPrefix}encodedValue"));
            Assert.False(await cache.ExistsAsync($"{specialPrefix}escapedString"));
            Assert.True(await cache.ExistsAsync($"unmatched{specialPrefix}entry"));
        }
    }

    public static IEnumerable<object[]> GetLineEndingPrefixes()
    {
        return
        [
            ["\n"],
            ["\r"],
            ["\r\n"]
        ];
    }

    public virtual async Task RemoveByPrefixAsync_WithLineEndingPrefix_TreatsAsLiteral(string lineEndingPrefix)
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            await cache.SetAsync("logEntry", 1000);
            await cache.SetAsync($"{lineEndingPrefix}parsedLine1", 2000);
            await cache.SetAsync($"{lineEndingPrefix}parsedLine2", 3000);

            int removed = await cache.RemoveByPrefixAsync(lineEndingPrefix);
            Assert.Equal(2, removed);
            Assert.False(await cache.ExistsAsync($"{lineEndingPrefix}parsedLine1"));
            Assert.False(await cache.ExistsAsync($"{lineEndingPrefix}parsedLine2"));
            Assert.True(await cache.ExistsAsync("logEntry"));
        }
    }

    public virtual async Task RemoveByPrefixAsync_FromScopedCache_RemovesOnlyScopedKeys(string prefixToRemove,
        int expectedRemovedCount)
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            var scopedCache = new ScopedCacheClient(cache, "scoped1");

            const string key = "snowboard";
            Assert.True(await cache.SetAsync(key, 1));
            Assert.True(await scopedCache.SetAsync(key, 1));

            Assert.Equal(1, (await cache.GetAsync<int>(key)).Value);
            Assert.Equal(1, (await scopedCache.GetAsync<int>(key)).Value);

            // Remove by prefix from scoped cache
            Assert.Equal(expectedRemovedCount, await scopedCache.RemoveByPrefixAsync(prefixToRemove));

            // Verify unscoped cache state
            Assert.True(await cache.ExistsAsync(key));

            // Verify scoped cache item was removed
            Assert.False(await scopedCache.ExistsAsync(key));
        }
    }

    public virtual async Task RemoveByPrefixAsync_NullOrEmptyPrefixWithScopedCache_RemovesCorrectKeys(string prefix)
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            var scopedCache = new ScopedCacheClient(cache, "scoped1");

            const string key = "snowboard";
            await cache.SetAsync(key, 1);
            await scopedCache.SetAsync(key, 1);

            // Remove by null/empty from scoped cache - should only remove within scope
            Assert.Equal(1, await scopedCache.RemoveByPrefixAsync(prefix));
            Assert.True(await cache.ExistsAsync(key));
            Assert.False(await scopedCache.ExistsAsync(key));

            // Add the scoped cache value back
            await scopedCache.SetAsync(key, 1);

            // Remove by null/empty from unscoped cache - should remove both unscoped and scoped
            Assert.Equal(2, await cache.RemoveByPrefixAsync(prefix));
            Assert.False(await cache.ExistsAsync(key));
            Assert.False(await scopedCache.ExistsAsync(key));
        }
    }

    public virtual async Task RemoveByPrefixAsync_PartialPrefixWithScopedCache_RemovesMatchingKeys()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            var scopedCache = new ScopedCacheClient(cache, "scoped1");

            const string key = "snowboard";
            await cache.SetAsync(key, 1);
            await scopedCache.SetAsync(key, 1);

            // Remove by partial prefix "s" from scoped cache
            Assert.Equal(1, await scopedCache.RemoveByPrefixAsync("s"));
            Assert.True(await cache.ExistsAsync(key));
            Assert.False(await scopedCache.ExistsAsync(key));

            // Add the scoped cache value back
            await scopedCache.SetAsync(key, 1);

            // Remove by partial prefix "s" from unscoped cache - should remove both
            Assert.Equal(2, await cache.RemoveByPrefixAsync("s"));
            Assert.False(await cache.ExistsAsync(key));
            Assert.False(await scopedCache.ExistsAsync(key));
        }
    }

    public virtual async Task RemoveByPrefixAsync_WithMultipleMatchingKeys_RemovesOnlyPrefixedKeys(int count)
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            const string prefix = "product:";
            const string unmatchedKey = "order";
            await cache.SetAsync(unmatchedKey, 1);

            await cache.SetAllAsync(Enumerable.Range(0, count).ToDictionary(i => $"{prefix}item{i}"));

            Assert.Equal(1, (await cache.GetAsync<int>($"{prefix}item1")).Value);
            Assert.Equal(1, (await cache.GetAsync<int>(unmatchedKey)).Value);

            // Verify non-existent prefix removal returns 0
            Assert.Equal(0, await cache.RemoveByPrefixAsync($"{prefix}doesntexist"));

            // Verify removal of all matching prefix keys
            Assert.Equal(count, await cache.RemoveByPrefixAsync(prefix));

            // Verify only unmatched key remains
            Assert.True(await cache.ExistsAsync(unmatchedKey));
        }
    }

    public virtual async Task RemoveIfEqualAsync_WithInvalidKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await cache.RemoveIfEqualAsync(null!, "value"));

            await Assert.ThrowsAsync<ArgumentException>(async () =>
                await cache.RemoveIfEqualAsync(String.Empty, "value"));
        }
    }

    public virtual async Task RemoveIfEqualAsync_WithMatchingValue_ReturnsTrueAndRemoves()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            Assert.True(await cache.AddAsync("session:active", "123"));

            Assert.True(await cache.RemoveIfEqualAsync("session:active", "123"));
            var result = await cache.GetAsync<string>("session:active");
            Assert.NotNull(result);
            Assert.False(result.HasValue);
        }
    }

    public virtual async Task RemoveIfEqualAsync_WithMismatchedValue_ReturnsFalseAndDoesNotRemove()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            Assert.True(await cache.AddAsync("remove-if-equal", "123"));
            var result = await cache.GetAsync<string>("remove-if-equal");
            Assert.NotNull(result);
            Assert.Equal("123", result.Value);

            Assert.False(await cache.RemoveIfEqualAsync("remove-if-equal", "789"));
            result = await cache.GetAsync<string>("remove-if-equal");
            Assert.NotNull(result);
            Assert.Equal("123", result.Value);
        }
    }

    public virtual async Task ReplaceAsync_WithExistingKey_ReturnsTrueAndReplacesValue()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            const string cacheKey = "replace-existing";

            // Add initial value
            Assert.True(await cache.AddAsync(cacheKey, "original"));
            var result = await cache.GetAsync<string>(cacheKey);
            Assert.NotNull(result);
            Assert.Equal("original", result.Value);

            // Replace value without expiration
            Assert.True(await cache.ReplaceAsync(cacheKey, "replaced"));
            result = await cache.GetAsync<string>(cacheKey);
            Assert.NotNull(result);
            Assert.Equal("replaced", result.Value);

            // Replace value with expiration
            Assert.True(await cache.ReplaceAsync(cacheKey, "with-expiration", TimeSpan.FromHours(1)));
            result = await cache.GetAsync<string>(cacheKey);
            Assert.NotNull(result);
            Assert.Equal("with-expiration", result.Value);
            var expiration = await cache.GetExpirationAsync(cacheKey);
            Assert.NotNull(expiration);
            Assert.True(expiration.Value > TimeSpan.Zero);
        }
    }

    public virtual async Task ReplaceAsync_WithNonExistentKey_ReturnsFalseAndDoesNotCreateKey()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            const string cacheKey = "non-existent";
            Assert.False(await cache.ReplaceAsync(cacheKey, "value"));
            var result = await cache.GetAsync<string>(cacheKey);
            Assert.False(result.HasValue);

            // Verify case-sensitivity: set keys with different cases, replace only exact matches
            await cache.SetAsync("TEST", 1);
            await cache.SetAsync("test", 2);

            Assert.True(await cache.ReplaceAsync("TEST", 10));
            Assert.Equal(10, (await cache.GetAsync<int>("TEST")).Value);
            Assert.Equal(2, (await cache.GetAsync<int>("test")).Value);

            Assert.True(await cache.ReplaceAsync("test", 20));
            Assert.Equal(10, (await cache.GetAsync<int>("TEST")).Value);
            Assert.Equal(20, (await cache.GetAsync<int>("test")).Value);
        }
    }

    public virtual async Task ReplaceAsync_WithInvalidKey_ThrowsException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () => await cache.ReplaceAsync(null!, 1));
            await Assert.ThrowsAsync<ArgumentException>(async () => await cache.ReplaceAsync(String.Empty, 1));
        }
    }

    public virtual async Task ReplaceAsync_WithExpiration_SetsExpirationCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            // Past expiration on existing key: should return false and remove the key
            Assert.True(await cache.AddAsync("replace-past-exp", "original"));
            Assert.True(await cache.ExistsAsync("replace-past-exp"));
            Assert.False(await cache.ReplaceAsync("replace-past-exp", "new-value", TimeSpan.FromMilliseconds(-1)));
            Assert.False(await cache.ExistsAsync("replace-past-exp"));
            Assert.False((await cache.GetAsync<string>("replace-past-exp")).HasValue);

            // Past expiration on non-existent key: should return false
            Assert.False(await cache.ReplaceAsync("replace-past-exp-nonexistent", "value", TimeSpan.FromMilliseconds(-1)));
            Assert.False(await cache.ExistsAsync("replace-past-exp-nonexistent"));

            // Zero expiration: should also be treated as expired
            Assert.True(await cache.AddAsync("replace-zero-exp", "original"));
            Assert.True(await cache.ExistsAsync("replace-zero-exp"));
            Assert.False(await cache.ReplaceAsync("replace-zero-exp", "new-value", TimeSpan.Zero));
            Assert.False(await cache.ExistsAsync("replace-zero-exp"));
            Assert.False((await cache.GetAsync<string>("replace-zero-exp")).HasValue);

            // Max expiration: should return true, key should exist with no expiration
            Assert.True(await cache.AddAsync("replace-max-exp", "original"));
            Assert.True(await cache.ReplaceAsync("replace-max-exp", "new-value", TimeSpan.MaxValue));
            Assert.True(await cache.ExistsAsync("replace-max-exp"));
            Assert.Equal("new-value", (await cache.GetAsync<string>("replace-max-exp")).Value);
            var expiration = await cache.GetExpirationAsync("replace-max-exp");
            Assert.Null(expiration); // TimeSpan.MaxValue means no expiration

            // Normal expiration: should return true, key should exist with correct expiration
            Assert.True(await cache.AddAsync("replace-normal-exp", "original"));
            Assert.True(await cache.ReplaceAsync("replace-normal-exp", "new-value", TimeSpan.FromHours(1)));
            Assert.True(await cache.ExistsAsync("replace-normal-exp"));
            Assert.Equal("new-value", (await cache.GetAsync<string>("replace-normal-exp")).Value);
            expiration = await cache.GetExpirationAsync("replace-normal-exp");
            Assert.NotNull(expiration);
            Assert.InRange(expiration.Value, TimeSpan.FromMinutes(59), TimeSpan.FromHours(1));

            // Null expiration: should succeed and remove expiration
            Assert.True(await cache.AddAsync("replace-null-exp", "original", TimeSpan.FromHours(1)));
            Assert.True(await cache.ExistsAsync("replace-null-exp"));
            expiration = await cache.GetExpirationAsync("replace-null-exp");
            Assert.NotNull(expiration);

            // Now replace without expiration - should succeed and remove expiration
            Assert.True(await cache.ReplaceAsync("replace-null-exp", "new-value"));
            Assert.True(await cache.ExistsAsync("replace-null-exp"));
            Assert.Equal("new-value", (await cache.GetAsync<string>("replace-null-exp")).Value);
            expiration = await cache.GetExpirationAsync("replace-null-exp");
            Assert.Null(expiration);
        }
    }

    public virtual async Task ReplaceIfEqualAsync_WithInvalidKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await cache.ReplaceIfEqualAsync(null!, "old", "new"));
            await Assert.ThrowsAsync<ArgumentException>(async () =>
                await cache.ReplaceIfEqualAsync(String.Empty, "old", "new"));
        }
    }

    public virtual async Task ReplaceIfEqualAsync_WithMatchingOldValue_ReturnsTrueAndReplacesValue()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            const string cacheKey = "workflow:state";
            Assert.True(await cache.AddAsync(cacheKey, "123"));

            Assert.True(await cache.ReplaceIfEqualAsync(cacheKey, "456", "123"));
            var result = await cache.GetAsync<string>(cacheKey);
            Assert.NotNull(result);
            Assert.Equal("456", result.Value);
        }
    }

    public virtual async Task ReplaceIfEqualAsync_WithMismatchedOldValue_ReturnsFalseAndDoesNotReplace()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            const string cacheKey = "replace-if-equal";
            Assert.True(await cache.AddAsync(cacheKey, "123"));
            var result = await cache.GetAsync<string>(cacheKey);
            Assert.NotNull(result);
            Assert.Equal("123", result.Value);

            Assert.False(await cache.ReplaceIfEqualAsync(cacheKey, "456", "789"));
            result = await cache.GetAsync<string>(cacheKey);
            Assert.NotNull(result);
            Assert.Equal("123", result.Value);

            // Verify case-sensitivity: different-cased keys are distinct, replace only exact match
            Assert.True(await cache.AddAsync("statusCode", 200));
            Assert.True(await cache.AddAsync("StatusCode", 201));
            Assert.True(await cache.AddAsync("STATUSCODE", 202));

            Assert.True(await cache.ReplaceIfEqualAsync("StatusCode", 299, 201));

            Assert.Equal(200, (await cache.GetAsync<int>("statusCode")).Value);
            Assert.Equal(299, (await cache.GetAsync<int>("StatusCode")).Value);
            Assert.Equal(202, (await cache.GetAsync<int>("STATUSCODE")).Value);
        }
    }

    public virtual async Task ReplaceIfEqualAsync_WithExpiration_SetsExpirationCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            // Past expiration on existing key with matching old value: should return false and remove the key
            Assert.True(await cache.AddAsync("replace-if-equal-past-exp", "old-value"));
            Assert.True(await cache.ExistsAsync("replace-if-equal-past-exp"));
            bool result = await cache.ReplaceIfEqualAsync("replace-if-equal-past-exp", "new-value", "old-value", TimeSpan.FromMilliseconds(-1));
            Assert.False(result);
            Assert.False(await cache.ExistsAsync("replace-if-equal-past-exp"));
            Assert.False((await cache.GetAsync<string>("replace-if-equal-past-exp")).HasValue);

            // Past expiration on non-existent key: should return false
            result = await cache.ReplaceIfEqualAsync("replace-if-equal-past-exp-nonexistent", "new-value", "old-value", TimeSpan.FromMilliseconds(-1));
            Assert.False(result);
            Assert.False(await cache.ExistsAsync("replace-if-equal-past-exp-nonexistent"));

            // Zero expiration: should also be treated as expired
            Assert.True(await cache.AddAsync("replace-if-equal-zero-exp", "old-value"));
            Assert.True(await cache.ExistsAsync("replace-if-equal-zero-exp"));
            result = await cache.ReplaceIfEqualAsync("replace-if-equal-zero-exp", "new-value", "old-value", TimeSpan.Zero);
            Assert.False(result);
            Assert.False(await cache.ExistsAsync("replace-if-equal-zero-exp"));
            Assert.False((await cache.GetAsync<string>("replace-if-equal-zero-exp")).HasValue);

            // Max expiration: should succeed and key should exist with no expiration
            Assert.True(await cache.AddAsync("replace-if-equal-max-exp", "old-value"));
            result = await cache.ReplaceIfEqualAsync("replace-if-equal-max-exp", "new-value", "old-value", TimeSpan.MaxValue);
            Assert.True(result);
            Assert.True(await cache.ExistsAsync("replace-if-equal-max-exp"));
            Assert.Equal("new-value", (await cache.GetAsync<string>("replace-if-equal-max-exp")).Value);
            var expiration = await cache.GetExpirationAsync("replace-if-equal-max-exp");
            Assert.Null(expiration); // TimeSpan.MaxValue means no expiration

            // Normal expiration: should succeed and key should exist with correct expiration
            Assert.True(await cache.AddAsync("replace-if-equal-normal-exp", "old-value"));
            Assert.Null(await cache.GetExpirationAsync("replace-if-equal-normal-exp"));
            result = await cache.ReplaceIfEqualAsync("replace-if-equal-normal-exp", "new-value", "old-value", TimeSpan.FromHours(1));
            Assert.True(result);
            Assert.True(await cache.ExistsAsync("replace-if-equal-normal-exp"));
            Assert.Equal("new-value", (await cache.GetAsync<string>("replace-if-equal-normal-exp")).Value);
            expiration = await cache.GetExpirationAsync("replace-if-equal-normal-exp");
            Assert.NotNull(expiration);
            Assert.InRange(expiration.Value, TimeSpan.FromMinutes(59), TimeSpan.FromHours(1));

            // Null expiration: should succeed and remove existing expiration
            Assert.True(await cache.AddAsync("replace-if-equal-null-exp", "old-value", TimeSpan.FromHours(1)));
            Assert.True(await cache.ExistsAsync("replace-if-equal-null-exp"));
            expiration = await cache.GetExpirationAsync("replace-if-equal-null-exp");
            Assert.NotNull(expiration);

            // Now replace without expiration - should succeed and remove existing TTL
            result = await cache.ReplaceIfEqualAsync("replace-if-equal-null-exp", "new-value", "old-value");
            Assert.True(result);
            Assert.True(await cache.ExistsAsync("replace-if-equal-null-exp"));
            Assert.Equal("new-value", (await cache.GetAsync<string>("replace-if-equal-null-exp")).Value);
            expiration = await cache.GetExpirationAsync("replace-if-equal-null-exp");
            Assert.Null(expiration); // Null expiration removes TTL
        }
    }

    public virtual async Task SetAllAsync_WithExpiration_SetsExpirationCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            // Past expiration (DateTime.MinValue): should not add keys and remove any existing
            Assert.True(await cache.SetAsync("setall-past-existing", "original"));
            Assert.True(await cache.ExistsAsync("setall-past-existing"));
            Assert.Equal(0,
                await cache.SetAllAsync(
                    new Dictionary<string, object> { { "expired1", 1 }, { "expired2", 2 }, { "setall-past-existing", "new" } },
                    DateTime.MinValue));
            Assert.False(await cache.ExistsAsync("expired1"));
            Assert.False(await cache.ExistsAsync("expired2"));
            Assert.False(await cache.ExistsAsync("setall-past-existing"));
            Assert.False((await cache.GetAsync<string>("setall-past-existing")).HasValue);

            // Past expiration with TimeSpan: should not add keys
            Assert.Equal(0,
                await cache.SetAllAsync(
                    new Dictionary<string, object> { { "expired-ts1", 1 }, { "expired-ts2", 2 } },
                    TimeSpan.FromMilliseconds(-1)));
            Assert.False(await cache.ExistsAsync("expired-ts1"));
            Assert.False(await cache.ExistsAsync("expired-ts2"));

            // Zero expiration: should also be treated as expired
            Assert.Equal(0,
                await cache.SetAllAsync(
                    new Dictionary<string, object> { { "zero-exp1", 1 }, { "zero-exp2", 2 } },
                    TimeSpan.Zero));
            Assert.False(await cache.ExistsAsync("zero-exp1"));
            Assert.False(await cache.ExistsAsync("zero-exp2"));

            // Max expiration: should succeed and keys should exist with no expiration
            Assert.Equal(2,
                await cache.SetAllAsync(
                    new Dictionary<string, object> { { "max-exp1", 1 }, { "max-exp2", 2 } },
                    TimeSpan.MaxValue));
            Assert.True(await cache.ExistsAsync("max-exp1"));
            Assert.True(await cache.ExistsAsync("max-exp2"));
            Assert.Equal(1, (await cache.GetAsync<int>("max-exp1")).Value);
            Assert.Equal(2, (await cache.GetAsync<int>("max-exp2")).Value);
            var expiration = await cache.GetExpirationAsync("max-exp1");
            Assert.Null(expiration); // TimeSpan.MaxValue means no expiration
            expiration = await cache.GetExpirationAsync("max-exp2");
            Assert.Null(expiration); // TimeSpan.MaxValue means no expiration

            // Normal expiration: should succeed and keys should exist with correct expiration
            var expiry = TimeSpan.FromHours(1);
            var items = new Dictionary<string, int> { { "normal-exp1", 1 }, { "normal-exp2", 2 } };
            await cache.SetAllAsync(items, expiry);
            Assert.True(await cache.ExistsAsync("normal-exp1"));
            Assert.True(await cache.ExistsAsync("normal-exp2"));
            Assert.Equal(1, (await cache.GetAsync<int>("normal-exp1")).Value);
            Assert.Equal(2, (await cache.GetAsync<int>("normal-exp2")).Value);
            expiration = await cache.GetExpirationAsync("normal-exp1");
            Assert.NotNull(expiration);
            Assert.InRange(expiration.Value, TimeSpan.FromMinutes(59), TimeSpan.FromHours(1));

            // Normal expiration with short delay actually expires
            expiry = TimeSpan.FromMilliseconds(50);
            items = new Dictionary<string, int> { { "itemId", 1 }, { "ItemId", 2 }, { "ITEMID", 3 } };
            await cache.SetAllAsync(items, expiry);

            var results = await cache.GetAllAsync<int>(["itemId", "ItemId", "ITEMID"]);
            Assert.Equal(3, results.Count);
            Assert.Equal(1, results["itemId"].Value);
            Assert.Equal(2, results["ItemId"].Value);
            Assert.Equal(3, results["ITEMID"].Value);

            // Add 10ms to the expiry to ensure the cache has expired as the delay window is not guaranteed to be exact.
            await Task.Delay(expiry.Add(TimeSpan.FromMilliseconds(10)));

            Assert.False(await cache.ExistsAsync("itemId"));
            Assert.False(await cache.ExistsAsync("ItemId"));
            Assert.False(await cache.ExistsAsync("ITEMID"));
        }
    }

    public virtual async Task SetAllAsync_WithInvalidItems_ValidatesCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            // Null items throws ArgumentNullException
            await Assert.ThrowsAsync<ArgumentNullException>(async () => await cache.SetAllAsync<string>(null));

            // Items containing empty key throws ArgumentException
            var itemsWithEmptyKey = new Dictionary<string, string> { { "key1", "value1" }, { String.Empty, "value2" } };
            await Assert.ThrowsAsync<ArgumentException>(async () => await cache.SetAllAsync(itemsWithEmptyKey));

            // Empty items collection returns 0 (not an error)
            int result = await cache.SetAllAsync(new Dictionary<string, string>());
            Assert.Equal(0, result);
        }
    }

    public virtual async Task SetAllExpirationAsync_WithInvalidItems_ValidatesCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            // Null expirations throws ArgumentNullException
            await Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await cache.SetAllExpirationAsync(null!));

            // Items containing empty key throws ArgumentException
            var itemsWithEmptyKey = new Dictionary<string, TimeSpan?> { { "key1", TimeSpan.FromMinutes(5) }, { String.Empty, TimeSpan.FromMinutes(10) } };
            await Assert.ThrowsAsync<ArgumentException>(async () => await cache.SetAllExpirationAsync(itemsWithEmptyKey));

            // Empty expirations collection doesn't throw (no-op)
            await cache.SetAllExpirationAsync(new Dictionary<string, TimeSpan?>());
        }
    }

    public virtual async Task SetAllExpirationAsync_WithExpiration_SetsExpirationCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            // Set up keys with various initial states
            await cache.SetAsync("set-expiration-key", 1);
            await cache.SetAsync("update-expiration-key", 2, TimeSpan.FromMinutes(5));
            await cache.SetAsync("remove-expiration-key", 3, TimeSpan.FromMinutes(10));

            // Verify initial state
            Assert.Null(await cache.GetExpirationAsync("set-expiration-key"));
            Assert.NotNull(await cache.GetExpirationAsync("update-expiration-key"));
            Assert.NotNull(await cache.GetExpirationAsync("remove-expiration-key"));

            var expirations = new Dictionary<string, TimeSpan?>
            {
                { "set-expiration-key", TimeSpan.FromMinutes(15) },
                { "update-expiration-key", TimeSpan.FromMinutes(30) },
                { "remove-expiration-key", null },
                { "nonexistent-key", TimeSpan.FromMinutes(20) }
            };

            await cache.SetAllExpirationAsync(expirations);

            // Verify expiration was set on key without prior expiration
            var setExpiration = await cache.GetExpirationAsync("set-expiration-key");
            Assert.NotNull(setExpiration);
            Assert.True(setExpiration.Value > TimeSpan.FromMinutes(14));
            Assert.True(setExpiration.Value <= TimeSpan.FromMinutes(15));

            // Verify expiration was updated on key with prior expiration
            var updateExpiration = await cache.GetExpirationAsync("update-expiration-key");
            Assert.NotNull(updateExpiration);
            Assert.True(updateExpiration.Value > TimeSpan.FromMinutes(29));
            Assert.True(updateExpiration.Value <= TimeSpan.FromMinutes(30));

            // Verify null removes expiration but key still exists
            Assert.Null(await cache.GetExpirationAsync("remove-expiration-key"));
            Assert.True(await cache.ExistsAsync("remove-expiration-key"));

            // Verify non-existent key was not created
            Assert.False(await cache.ExistsAsync("nonexistent-key"));
            Assert.Null(await cache.GetExpirationAsync("nonexistent-key"));
        }
    }

    public virtual async Task SetAllExpirationAsync_WithLargeNumberOfKeys_SetsAllExpirations(int count)
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var keys = new List<string>();
            for (int i = 0; i < count; i++)
            {
                string key = $"perf-test-key-{i}";
                keys.Add(key);
                await cache.SetAsync(key, i);
            }

            var expirations = new Dictionary<string, TimeSpan?>();
            for (int i = 0; i < count; i++)
            {
                expirations[keys[i]] = TimeSpan.FromMinutes(i % 60 + 1);
            }

            var sw = Stopwatch.StartNew();
            await cache.SetAllExpirationAsync(expirations);
            sw.Stop();

            _logger.LogInformation("Set All Expiration Time ({Count} keys): {Elapsed:g}", count, sw.Elapsed);

            // Verify a sample of keys
            var key0Expiration = await cache.GetExpirationAsync(keys[0]);
            Assert.NotNull(key0Expiration);
            Assert.True(key0Expiration.Value <= TimeSpan.FromMinutes(1));

            int keySampleIndex = count / 2;
            var keySampleExpiration = await cache.GetExpirationAsync(keys[keySampleIndex]);
            Assert.NotNull(keySampleExpiration);
            Assert.True(keySampleExpiration.Value <= TimeSpan.FromMinutes(keySampleIndex % 60 + 1));
        }
    }

    public virtual async Task SetAsync_WithComplexObject_StoresCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var dt = DateTimeOffset.Now;
            var value = new MyData { Type = "test", Date = dt, Message = "Hello World" };

            await cache.SetAsync("user:profile", value);

            Assert.True(await cache.ExistsAsync("user:profile"));
            var cachedValue = await cache.GetAsync<MyData>("user:profile");
            Assert.NotNull(cachedValue);
            Assert.True(cachedValue.HasValue);
        }
    }

    public virtual async Task SetAsync_WithExpiration_SetsExpirationCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var utcNow = DateTime.UtcNow;
            var expires = DateTime.MaxValue - utcNow.AddDays(1);
            Assert.True(await cache.SetAsync("test1", 1, expires));
            Assert.Equal(1, (await cache.GetAsync<int>("test1")).Value);
            var actualExpiration = await cache.GetExpirationAsync("test1");
            Assert.NotNull(actualExpiration);
            Assert.InRange(actualExpiration.Value, expires.Subtract(TimeSpan.FromSeconds(10)), expires);

            // MinValue expires items.
            Assert.False(await cache.SetAsync("test2", 1, DateTime.MinValue));
            Assert.False(await cache.ExistsAsync("test2"));

            // MaxValue never expires - returns null from GetExpirationAsync.
            Assert.True(await cache.SetAsync("test3", 1, DateTime.MaxValue));
            Assert.Equal(1, (await cache.GetAsync<int>("test3")).Value);
            actualExpiration = await cache.GetExpirationAsync("test3");
            Assert.Null(actualExpiration); // No expiration set means null

            // Really high expiration value (still hits DateTime.MaxValue via SafeAdd).
            Assert.True(await cache.SetAsync("test4", 1, DateTime.MaxValue - utcNow.AddDays(-1)));
            Assert.Equal(1, (await cache.GetAsync<int>("test4")).Value);
            actualExpiration = await cache.GetExpirationAsync("test4");
            Assert.Null(actualExpiration); // Also results in DateTime.MaxValue (no expiration)

            // No Expiration
            Assert.True(await cache.SetAsync("test5", 1));
            Assert.Null(await cache.GetExpirationAsync("test5"));

            // Expire time right now
            Assert.False(await cache.SetAsync("test9", 1, utcNow));
            Assert.False(await cache.ExistsAsync("test9"));
            Assert.Null(await cache.GetExpirationAsync("test9"));

            // Null expiration: should succeed and remove expiration
            Assert.True(await cache.SetAsync("set-null-exp", "value", TimeSpan.FromHours(1)));
            Assert.True(await cache.ExistsAsync("set-null-exp"));
            Assert.Equal("value", (await cache.GetAsync<string>("set-null-exp")).Value);
            var nullExpExpiration = await cache.GetExpirationAsync("set-null-exp");
            Assert.NotNull(nullExpExpiration);

            // Now set without expiration - should succeed and remove expiration
            Assert.True(await cache.SetAsync("set-null-exp", "new-value"));
            Assert.True(await cache.ExistsAsync("set-null-exp"));
            Assert.Equal("new-value", (await cache.GetAsync<string>("set-null-exp")).Value);
            nullExpExpiration = await cache.GetExpirationAsync("set-null-exp");
            Assert.Null(nullExpExpiration);
        }
    }

    public virtual async Task SetExpirationAsync_WithExpiration_SetsExpirationCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var utcNow = DateTime.UtcNow;

            // Past expiration: should remove the key
            await cache.SetAsync("set-expiration-past-exp", "test-value");
            Assert.True(await cache.ExistsAsync("set-expiration-past-exp"));
            await cache.SetExpirationAsync("set-expiration-past-exp", TimeSpan.FromMilliseconds(-1));
            Assert.False(await cache.ExistsAsync("set-expiration-past-exp"));
            Assert.False((await cache.GetAsync<string>("set-expiration-past-exp")).HasValue);

            // Past expiration with DateTime.MinValue: should remove the key
            await cache.SetAsync("set-expiration-min-value", "test-value");
            Assert.True(await cache.ExistsAsync("set-expiration-min-value"));
            await cache.SetExpirationAsync("set-expiration-min-value", DateTime.MinValue);
            Assert.False(await cache.ExistsAsync("set-expiration-min-value"));
            Assert.False((await cache.GetAsync<string>("set-expiration-min-value")).HasValue);

            // Max expiration with DateTime.MaxValue: should remove expiration (return null)
            await cache.SetAsync("set-expiration-max-value", "test-value");
            await cache.SetExpirationAsync("set-expiration-max-value", DateTime.MaxValue);
            Assert.True(await cache.ExistsAsync("set-expiration-max-value"));
            var expiration = await cache.GetExpirationAsync("set-expiration-max-value");
            Assert.Null(expiration); // DateTime.MaxValue means no expiration

            // Normal expiration with DateTime: should set expiration correctly
            await cache.SetAsync("set-expiration-datetime", "test-value");
            Assert.Null(await cache.GetExpirationAsync("set-expiration-datetime"));
            var expirationDateTime = utcNow.AddHours(1);
            await cache.SetExpirationAsync("set-expiration-datetime", expirationDateTime);
            Assert.True(await cache.ExistsAsync("set-expiration-datetime"));
            expiration = await cache.GetExpirationAsync("set-expiration-datetime");
            Assert.NotNull(expiration);
            Assert.InRange(expiration.Value, expirationDateTime - expirationDateTime.Subtract(TimeSpan.FromSeconds(5)),
                expirationDateTime - utcNow);

            // Normal expiration with TimeSpan: should set expiration correctly
            await cache.SetAsync("set-expiration-timespan", "test-value");
            Assert.Null(await cache.GetExpirationAsync("set-expiration-timespan"));
            await cache.SetExpirationAsync("set-expiration-timespan", TimeSpan.FromHours(1));
            Assert.True(await cache.ExistsAsync("set-expiration-timespan"));
            expiration = await cache.GetExpirationAsync("set-expiration-timespan");
            Assert.NotNull(expiration);
            Assert.InRange(expiration.Value, TimeSpan.FromMinutes(59), TimeSpan.FromHours(1));

            // Null expiration removal is tested via SetAllExpirationAsync since SetExpirationAsync
            // does not accept nullable TimeSpan - see SetAllExpirationAsync_WithExpiration_SetsExpirationCorrectly
        }
    }

    public virtual async Task SetAsync_WithInvalidKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () => await cache.SetAsync(null, "value"));
            await Assert.ThrowsAsync<ArgumentException>(async () => await cache.SetAsync(String.Empty, "value"));
        }
    }

    public virtual async Task SetAsync_WithLargeNumbersAndExpiration_PreservesValues()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var minExpiration = TimeSpan.FromHours(1).Add(TimeSpan.FromMinutes(59)).Add(TimeSpan.FromSeconds(55));
            double value = 2 * 1000 * 1000 * 1000;
            Assert.True(await cache.SetAsync("test", value, TimeSpan.FromHours(2)));
            Assert.Equal(value, await cache.GetAsync<double>("test", 0));
            Assert.InRange((await cache.GetExpirationAsync("test")).GetValueOrDefault(), minExpiration, TimeSpan.FromHours(2));

            double lowerValue = value - 1000;
            Assert.Equal(1000, await cache.SetIfLowerAsync("test", lowerValue, TimeSpan.FromHours(2)));
            Assert.Equal(lowerValue, await cache.GetAsync<double>("test", 0));
            Assert.InRange((await cache.GetExpirationAsync("test")).GetValueOrDefault(), minExpiration, TimeSpan.FromHours(2));

            Assert.Equal(0, await cache.SetIfLowerAsync("test", value, TimeSpan.FromHours(2)));
            Assert.Equal(lowerValue, await cache.GetAsync<double>("test", 0));
            Assert.InRange((await cache.GetExpirationAsync("test")).GetValueOrDefault(), minExpiration, TimeSpan.FromHours(2));

            Assert.Equal(1000, await cache.SetIfHigherAsync("test", value, TimeSpan.FromHours(2)));
            Assert.Equal(value, await cache.GetAsync<double>("test", 0));
            Assert.InRange((await cache.GetExpirationAsync("test")).GetValueOrDefault(), minExpiration, TimeSpan.FromHours(2));

            Assert.Equal(0, await cache.SetIfHigherAsync("test", lowerValue, TimeSpan.FromHours(2)));
            Assert.Equal(value, await cache.GetAsync<double>("test", 0));
            Assert.InRange((await cache.GetExpirationAsync("test")).GetValueOrDefault(), minExpiration, TimeSpan.FromHours(2));
        }
    }

    public virtual async Task SetAsync_WithNullValue_StoresAsNullValue()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            // Test null reference type
            await cache.SetAsync<SimpleModel>("nullable", null);
            var nullCacheValue = await cache.GetAsync<SimpleModel>("nullable");
            Assert.True(nullCacheValue.HasValue);
            Assert.True(nullCacheValue.IsNull);
            Assert.True(await cache.ExistsAsync("nullable"));

            // Test null value type
            Assert.False(await cache.ExistsAsync("nullableInt"));
            await cache.SetAsync<int?>("nullableInt", null);
            var nullIntCacheValue = await cache.GetAsync<int?>("nullableInt");
            Assert.True(nullIntCacheValue.HasValue);
            Assert.True(nullIntCacheValue.IsNull);
            Assert.True(await cache.ExistsAsync("nullableInt"));
        }
    }

    public virtual async Task SetAsync_WithScopedCaches_IsolatesKeys()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            // Test different scopes isolate keys
            var scopedCache1 = new ScopedCacheClient(cache, "scoped1");
            var scopedCache2 = new ScopedCacheClient(cache, "scoped2");

            await cache.SetAsync("test", 1);
            await scopedCache1.SetAsync("test", 2);
            await scopedCache2.SetAsync("test", 3);

            Assert.Equal(1, (await cache.GetAsync<int>("test")).Value);
            Assert.Equal(2, (await scopedCache1.GetAsync<int>("test")).Value);
            Assert.Equal(3, (await scopedCache2.GetAsync<int>("test")).Value);

            // Test nested scopes preserve hierarchy
            var nestedScopedCache1 = new ScopedCacheClient(scopedCache1, "nested");
            await nestedScopedCache1.SetAsync("test", 4);

            Assert.Equal(4, (await nestedScopedCache1.GetAsync<int>("test")).Value);
            Assert.Equal(4, (await scopedCache1.GetAsync<int>("nested:test")).Value);
            Assert.Equal(4, (await cache.GetAsync<int>("scoped1:nested:test")).Value);

            // Test case-sensitive scopes maintain distinct namespaces
            var scopedLower = new ScopedCacheClient(cache, "tenant");
            var scopedTitle = new ScopedCacheClient(cache, "Tenant");
            var scopedUpper = new ScopedCacheClient(cache, "TENANT");

            await scopedLower.SetAsync("dataId", "lower");
            await scopedTitle.SetAsync("dataId", "title");
            await scopedUpper.SetAsync("dataId", "upper");

            Assert.Equal("lower", (await scopedLower.GetAsync<string>("dataId")).Value);
            Assert.Equal("title", (await scopedTitle.GetAsync<string>("dataId")).Value);
            Assert.Equal("upper", (await scopedUpper.GetAsync<string>("dataId")).Value);

            // Test case-sensitive keys create distinct entries
            await cache.SetAsync("productId", 100);
            await cache.SetAsync("ProductId", 200);
            await cache.SetAsync("PRODUCTID", 300);

            Assert.Equal(100, (await cache.GetAsync<int>("productId")).Value);
            Assert.Equal(200, (await cache.GetAsync<int>("ProductId")).Value);
            Assert.Equal(300, (await cache.GetAsync<int>("PRODUCTID")).Value);
        }
    }

    public virtual async Task SetAsync_WithShortExpiration_ExpiresCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var expiresAt = DateTime.UtcNow.AddMilliseconds(100);
            bool success = await cache.SetAsync("test", 1, expiresAt);
            Assert.True(success);
            success = await cache.SetAsync("test2", 1, expiresAt.AddMilliseconds(50));
            Assert.True(success);
            Assert.Equal(1, (await cache.GetAsync<int>("test")).Value);
            Assert.True((await cache.GetExpirationAsync("test")).Value < TimeSpan.FromSeconds(1));

            await Task.Delay(200);
            Assert.False((await cache.GetAsync<int>("test")).HasValue);
            Assert.False((await cache.GetAsync<int>("test2")).HasValue);
        }
    }

    public virtual async Task SetExpirationAsync_WithInvalidKey_ThrowsArgumentException()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await cache.SetExpirationAsync(null, TimeSpan.FromMinutes(1)));

            await Assert.ThrowsAsync<ArgumentException>(async () =>
                await cache.SetExpirationAsync(String.Empty, TimeSpan.FromMinutes(1)));
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

    public virtual async Task SetIfHigherAsync_WithLargeNumbers_HandlesCorrectly()
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

    public virtual async Task SetIfHigherAsync_WithExpiration_SetsExpirationCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            // Past expiration on existing key: should return -1 and remove the key
            await cache.SetAsync("set-if-higher-past-exp", 100.0);
            Assert.True(await cache.ExistsAsync("set-if-higher-past-exp"));
            double result = await cache.SetIfHigherAsync("set-if-higher-past-exp", 200.0, TimeSpan.FromMilliseconds(-1));
            Assert.Equal(-1, result);
            Assert.False(await cache.ExistsAsync("set-if-higher-past-exp"));
            Assert.False((await cache.GetAsync<double>("set-if-higher-past-exp")).HasValue);

            // Past expiration on non-existent key: should return -1
            result = await cache.SetIfHigherAsync("set-if-higher-past-exp-nonexistent", 100.0, TimeSpan.FromMilliseconds(-1));
            Assert.Equal(-1, result);
            Assert.False(await cache.ExistsAsync("set-if-higher-past-exp-nonexistent"));

            // Zero expiration: should also be treated as expired
            await cache.SetAsync("set-if-higher-zero-exp", 100.0);
            Assert.True(await cache.ExistsAsync("set-if-higher-zero-exp"));
            result = await cache.SetIfHigherAsync("set-if-higher-zero-exp", 200.0, TimeSpan.Zero);
            Assert.Equal(-1, result);
            Assert.False(await cache.ExistsAsync("set-if-higher-zero-exp"));
            Assert.False((await cache.GetAsync<double>("set-if-higher-zero-exp")).HasValue);

            // Max expiration: should succeed and key should exist with no expiration
            result = await cache.SetIfHigherAsync("set-if-higher-max-exp", 100.0, TimeSpan.MaxValue);
            Assert.Equal(100.0, result);
            Assert.True(await cache.ExistsAsync("set-if-higher-max-exp"));
            Assert.Equal(100.0, (await cache.GetAsync<double>("set-if-higher-max-exp")).Value);
            var expiration = await cache.GetExpirationAsync("set-if-higher-max-exp");
            Assert.Null(expiration); // TimeSpan.MaxValue means no expiration

            // Normal expiration: should succeed and key should exist with correct expiration
            result = await cache.SetIfHigherAsync("set-if-higher-normal-exp", 100.0, TimeSpan.FromHours(1));
            Assert.Equal(100.0, result);
            Assert.True(await cache.ExistsAsync("set-if-higher-normal-exp"));
            Assert.Equal(100.0, (await cache.GetAsync<double>("set-if-higher-normal-exp")).Value);
            expiration = await cache.GetExpirationAsync("set-if-higher-normal-exp");
            Assert.NotNull(expiration);
            Assert.InRange(expiration.Value, TimeSpan.FromMinutes(59), TimeSpan.FromHours(1));

            // Test with long overload as well
            long longResult = await cache.SetIfHigherAsync("set-if-higher-long-exp", 100L, TimeSpan.FromHours(1));
            Assert.Equal(100L, longResult);
            Assert.True(await cache.ExistsAsync("set-if-higher-long-exp"));
            expiration = await cache.GetExpirationAsync("set-if-higher-long-exp");
            Assert.NotNull(expiration);
            Assert.InRange(expiration.Value, TimeSpan.FromMinutes(59), TimeSpan.FromHours(1));

            // Null expiration (double): calling with null should preserve existing expiration
            result = await cache.SetIfHigherAsync("set-if-higher-null-exp", 100.0, TimeSpan.FromHours(1));
            Assert.Equal(100.0, result);
            Assert.True(await cache.ExistsAsync("set-if-higher-null-exp"));
            expiration = await cache.GetExpirationAsync("set-if-higher-null-exp");
            Assert.NotNull(expiration);

            // Now call without expiration with higher value - should succeed and remove existing TTL
            // Returns the difference (200 - 100 = 100), not the new value
            result = await cache.SetIfHigherAsync("set-if-higher-null-exp", 200.0);
            Assert.Equal(100.0, result);
            Assert.True(await cache.ExistsAsync("set-if-higher-null-exp"));
            Assert.Equal(200.0, (await cache.GetAsync<double>("set-if-higher-null-exp")).Value);
            expiration = await cache.GetExpirationAsync("set-if-higher-null-exp");
            Assert.Null(expiration); // Null expiration removes TTL

            // Null expiration (long): calling with null should remove existing expiration
            longResult = await cache.SetIfHigherAsync("set-if-higher-null-exp-long", 100L, TimeSpan.FromHours(1));
            Assert.Equal(100L, longResult);
            Assert.True(await cache.ExistsAsync("set-if-higher-null-exp-long"));
            expiration = await cache.GetExpirationAsync("set-if-higher-null-exp-long");
            Assert.NotNull(expiration);

            // Now call without expiration with higher value - should succeed and remove existing TTL
            // Returns the difference (200 - 100 = 100), not the new value
            longResult = await cache.SetIfHigherAsync("set-if-higher-null-exp-long", 200L);
            Assert.Equal(100L, longResult);
            Assert.True(await cache.ExistsAsync("set-if-higher-null-exp-long"));
            Assert.Equal(200L, (await cache.GetAsync<long>("set-if-higher-null-exp-long")).Value);
            expiration = await cache.GetExpirationAsync("set-if-higher-null-exp-long");
            Assert.Null(expiration); // Null expiration removes TTL
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

    public virtual async Task SetIfLowerAsync_WithLargeNumbers_HandlesCorrectly()
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

    public virtual async Task SetIfLowerAsync_WithExpiration_SetsExpirationCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            // Past expiration on existing key: should return -1 and remove the key
            await cache.SetAsync("set-if-lower-past-exp", 100.0);
            Assert.True(await cache.ExistsAsync("set-if-lower-past-exp"));
            double result = await cache.SetIfLowerAsync("set-if-lower-past-exp", 50.0, TimeSpan.FromMilliseconds(-1));
            Assert.Equal(-1, result);
            Assert.False(await cache.ExistsAsync("set-if-lower-past-exp"));
            Assert.False((await cache.GetAsync<double>("set-if-lower-past-exp")).HasValue);

            // Past expiration on non-existent key: should return -1
            result = await cache.SetIfLowerAsync("set-if-lower-past-exp-nonexistent", 100.0, TimeSpan.FromMilliseconds(-1));
            Assert.Equal(-1, result);
            Assert.False(await cache.ExistsAsync("set-if-lower-past-exp-nonexistent"));

            // Zero expiration: should also be treated as expired
            await cache.SetAsync("set-if-lower-zero-exp", 100.0);
            Assert.True(await cache.ExistsAsync("set-if-lower-zero-exp"));
            result = await cache.SetIfLowerAsync("set-if-lower-zero-exp", 50.0, TimeSpan.Zero);
            Assert.Equal(-1, result);
            Assert.False(await cache.ExistsAsync("set-if-lower-zero-exp"));
            Assert.False((await cache.GetAsync<double>("set-if-lower-zero-exp")).HasValue);

            // Max expiration: should succeed and key should exist with no expiration
            result = await cache.SetIfLowerAsync("set-if-lower-max-exp", 100.0, TimeSpan.MaxValue);
            Assert.Equal(100.0, result);
            Assert.True(await cache.ExistsAsync("set-if-lower-max-exp"));
            Assert.Equal(100.0, (await cache.GetAsync<double>("set-if-lower-max-exp")).Value);
            var expiration = await cache.GetExpirationAsync("set-if-lower-max-exp");
            Assert.Null(expiration); // TimeSpan.MaxValue means no expiration

            // Normal expiration: should succeed and key should exist with correct expiration
            result = await cache.SetIfLowerAsync("set-if-lower-normal-exp", 100.0, TimeSpan.FromHours(1));
            Assert.Equal(100.0, result);
            Assert.True(await cache.ExistsAsync("set-if-lower-normal-exp"));
            Assert.Equal(100.0, (await cache.GetAsync<double>("set-if-lower-normal-exp")).Value);
            expiration = await cache.GetExpirationAsync("set-if-lower-normal-exp");
            Assert.NotNull(expiration);
            Assert.InRange(expiration.Value, TimeSpan.FromMinutes(59), TimeSpan.FromHours(1));

            // Test with long overload as well
            long longResult = await cache.SetIfLowerAsync("set-if-lower-long-exp", 100L, TimeSpan.FromHours(1));
            Assert.Equal(100L, longResult);
            Assert.True(await cache.ExistsAsync("set-if-lower-long-exp"));
            expiration = await cache.GetExpirationAsync("set-if-lower-long-exp");
            Assert.NotNull(expiration);
            Assert.InRange(expiration.Value, TimeSpan.FromMinutes(59), TimeSpan.FromHours(1));

            // Null expiration (double): calling with null should remove existing expiration
            result = await cache.SetIfLowerAsync("set-if-lower-null-exp", 100.0, TimeSpan.FromHours(1));
            Assert.Equal(100.0, result);
            Assert.True(await cache.ExistsAsync("set-if-lower-null-exp"));
            expiration = await cache.GetExpirationAsync("set-if-lower-null-exp");
            Assert.NotNull(expiration);

            // Now call without expiration with lower value - should succeed and remove existing TTL
            // Returns the difference (100 - 50 = 50), not the new value
            result = await cache.SetIfLowerAsync("set-if-lower-null-exp", 50.0);
            Assert.Equal(50.0, result);
            Assert.True(await cache.ExistsAsync("set-if-lower-null-exp"));
            Assert.Equal(50.0, (await cache.GetAsync<double>("set-if-lower-null-exp")).Value);
            expiration = await cache.GetExpirationAsync("set-if-lower-null-exp");
            Assert.Null(expiration); // Null expiration removes TTL

            // Null expiration (long): calling with null should remove existing expiration
            longResult = await cache.SetIfLowerAsync("set-if-lower-null-exp-long", 100L, TimeSpan.FromHours(1));
            Assert.Equal(100L, longResult);
            Assert.True(await cache.ExistsAsync("set-if-lower-null-exp-long"));
            expiration = await cache.GetExpirationAsync("set-if-lower-null-exp-long");
            Assert.NotNull(expiration);

            // Now call without expiration with lower value - should succeed and remove existing TTL
            // Returns the difference (100 - 50 = 50), not the new value
            longResult = await cache.SetIfLowerAsync("set-if-lower-null-exp-long", 50L);
            Assert.Equal(50L, longResult);
            Assert.True(await cache.ExistsAsync("set-if-lower-null-exp-long"));
            Assert.Equal(50L, (await cache.GetAsync<long>("set-if-lower-null-exp-long")).Value);
            expiration = await cache.GetExpirationAsync("set-if-lower-null-exp-long");
            Assert.Null(expiration); // Null expiration removes TTL
        }
    }

    public virtual async Task SetUnixTimeSecondsAsync_WithUtcDateTime_StoresCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            DateTime value = DateTime.UtcNow.Floor(TimeSpan.FromSeconds(1));
            long unixTimeValue = value.ToUnixTimeSeconds();

            Assert.True(await cache.SetUnixTimeSecondsAsync("test", value));
            Assert.Equal(unixTimeValue, await cache.GetAsync<long>("test", 0));
        }
    }

    public virtual async Task GetUnixTimeSecondsAsync_WithUtcDateTime_ReturnsCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            DateTime value = DateTime.UtcNow.Floor(TimeSpan.FromSeconds(1));

            await cache.SetUnixTimeSecondsAsync("test", value);
            var actual = await cache.GetUnixTimeSecondsAsync("test");

            Assert.Equal(value.Ticks, actual.Ticks);
            Assert.Equal(TimeSpan.Zero, actual.Offset);
        }
    }

    public virtual async Task SetUnixTimeMillisecondsAsync_WithLocalDateTime_StoresCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            DateTime value = DateTime.Now.Floor(TimeSpan.FromMilliseconds(1));
            long unixTimeValue = value.ToUnixTimeMilliseconds();

            Assert.True(await cache.SetUnixTimeMillisecondsAsync("test", value));
            Assert.Equal(unixTimeValue, await cache.GetAsync<long>("test", 0));
        }
    }

    public virtual async Task GetUnixTimeMillisecondsAsync_WithLocalDateTime_ReturnsCorrectly()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            DateTime value = DateTime.Now.Floor(TimeSpan.FromMilliseconds(1));

            await cache.SetUnixTimeMillisecondsAsync("test", value);
            var actual = (await cache.GetUnixTimeMillisecondsAsync("test")).ToLocalTime();

            Assert.Equal(value.Ticks, actual.Ticks);
        }
    }

    public virtual async Task GetUnixTimeMillisecondsAsync_WithUtcDateTime_ReturnsCorrectly()
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
            var actual = await cache.GetUnixTimeMillisecondsAsync("test");

            Assert.Equal(unixTimeValue, await cache.GetAsync<long>("test", 0));
            Assert.Equal(value, actual);
        }
    }

    /// <summary>
    /// Measures cache operation throughput by performing 10,000 iterations of Set/Get operations with assertions.
    /// Tests multiple primitive types (int, bool) and validates correctness during performance measurement.
    /// </summary>
    public virtual async Task CacheOperations_WithMultipleTypes_MeasuresThroughput()
    {
        var cache = GetCacheClient();
        if (cache is null)
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
                Assert.False((await cache.GetAsync<int>("test2")).HasValue);
                Assert.True((await cache.GetAsync<bool>("flag")).Value);
            }

            sw.Stop();
            _logger.LogInformation("Cache throughput: {Operations} operations in {Elapsed}ms ({Rate} ops/sec)",
                itemCount * 5, sw.ElapsedMilliseconds, itemCount * 5 / sw.Elapsed.TotalSeconds);
        }
    }

    /// <summary>
    /// Measures cache throughput with simple Set/Get operations using unique keys.
    /// Separates Set and Get operations for independent throughput measurement without assertions.
    /// </summary>
    public virtual async Task CacheOperations_WithRepeatedSetAndGet_MeasuresThroughput()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            const int iterations = 1000;
            var sw = Stopwatch.StartNew();

            for (int i = 0; i < iterations; i++)
            {
                await cache.SetAsync($"key{i}", i);
            }

            for (int i = 0; i < iterations; i++)
            {
                await cache.GetAsync<int>($"key{i}");
            }

            sw.Stop();
            _logger.LogInformation("Cache throughput: {Operations} operations in {Elapsed}ms ({Rate} ops/sec)",
                iterations * 2, sw.ElapsedMilliseconds, iterations * 2 / sw.Elapsed.TotalSeconds);
        }
    }

    /// <summary>
    /// Measures serialization throughput with simple objects (10,000 iterations).
    /// Tests Set/Get operations with assertions to validate serialization correctness under load.
    /// </summary>
    public virtual async Task Serialization_WithSimpleObjectsAndValidation_MeasuresThroughput()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var sw = Stopwatch.StartNew();
            const int iterations = 10000;
            for (int i = 0; i < iterations; i++)
            {
                await cache.SetAsync("test", new SimpleModel { Data1 = "Hello", Data2 = 12 });
                var model = await cache.GetAsync<SimpleModel>("test");
                Assert.True(model.HasValue);
                Assert.Equal("Hello", model.Value.Data1);
                Assert.Equal(12, model.Value.Data2);
            }

            sw.Stop();
            _logger.LogInformation("Cache throughput: {Operations} operations in {Elapsed}ms ({Rate} ops/sec)",
                iterations * 2, sw.ElapsedMilliseconds, iterations * 2 / sw.Elapsed.TotalSeconds);
        }
    }

    /// <summary>
    /// Measures serialization throughput with complex nested objects (10,000 iterations).
    /// Tests objects with nested models, lists, and dictionaries while validating correctness.
    /// </summary>
    public virtual async Task Serialization_WithComplexObjectsAndValidation_MeasuresThroughput()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            var sw = Stopwatch.StartNew();
            const int itemCount = 10000;
            for (int i = 0; i < itemCount; i++)
            {
                await cache.SetAsync("test",
                    new ComplexModel
                    {
                        Data1 = "Hello",
                        Data2 = 12,
                        Data3 = true,
                        Simple = new SimpleModel { Data1 = "hi", Data2 = 13 },
                        Simples =
                            new List<SimpleModel>
                            {
                                new SimpleModel { Data1 = "hey", Data2 = 45 },
                                new SimpleModel { Data1 = "next", Data2 = 3423 }
                            },
                        DictionarySimples =
                            new Dictionary<string, SimpleModel> { { "sdf", new SimpleModel { Data1 = "Sachin" } } },
                        DerivedDictionarySimples =
                            new SampleDictionary<string, SimpleModel>
                            {
                                { "sdf", new SimpleModel { Data1 = "Sachin" } }
                            }
                    });

                var model = await cache.GetAsync<ComplexModel>("test");
                Assert.True(model.HasValue);
                Assert.Equal("Hello", model.Value.Data1);
                Assert.Equal(12, model.Value.Data2);
            }

            sw.Stop();
            _logger.LogInformation("Cache throughput: {Operations} operations in {Elapsed}ms ({Rate} ops/sec)",
                itemCount * 2, sw.ElapsedMilliseconds, itemCount * 2 / sw.Elapsed.TotalSeconds);
        }
    }

    /// <summary>
    /// Measures SetAllAsync/GetAllAsync throughput with 9,999 keys in a single batch operation.
    /// Tests bulk insert and retrieval performance while validating data correctness.
    /// </summary>
    public virtual async Task SetAllAsync_WithLargeNumberOfKeys_MeasuresThroughput()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            const int keyCount = 9999;
            var items = new Dictionary<string, int>();
            for (int i = 0; i < keyCount; i++)
                items[$"key{i}"] = i;

            var sw = Stopwatch.StartNew();

            int result = await cache.SetAllAsync(items, TimeSpan.FromHours(1));
            Assert.Equal(keyCount, result);

            var keys = new List<string>();
            for (int i = 0; i < keyCount; i++)
                keys.Add($"key{i}");

            var results = await cache.GetAllAsync<int>(keys);
            Assert.Equal(keyCount, results.Count);

            sw.Stop();

            for (int i = 0; i < keyCount; i++)
                Assert.Equal(i, results[$"key{i}"].Value);

            _logger.LogInformation("Cache throughput: {Operations} operations in {Elapsed}ms ({Rate} ops/sec)",
                keyCount * 2, sw.ElapsedMilliseconds, keyCount * 2 / sw.Elapsed.TotalSeconds);
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
