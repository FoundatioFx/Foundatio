using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Foundatio.Tests.Caching;

public abstract partial class CacheClientTestsBase
{
    public virtual async Task SetAllExpiration_WithMultipleKeys_SetsExpirationForAll()
    {
        // Arrange
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            await cache.SetAsync("key1", 1);
            await cache.SetAsync("key2", 2);
            await cache.SetAsync("key3", 3);

            var expirations = new Dictionary<string, TimeSpan?>
            {
                { "key1", TimeSpan.FromMinutes(5) },
                { "key2", TimeSpan.FromMinutes(10) },
                { "key3", TimeSpan.FromMinutes(15) }
            };

            // Act
            await cache.SetAllExpirationAsync(expirations);

            // Assert
            var key1Expiration = await cache.GetExpirationAsync("key1");
            Assert.NotNull(key1Expiration);
            Assert.True(key1Expiration.Value > TimeSpan.FromMinutes(4));
            Assert.True(key1Expiration.Value <= TimeSpan.FromMinutes(5));

            var key2Expiration = await cache.GetExpirationAsync("key2");
            Assert.NotNull(key2Expiration);
            Assert.True(key2Expiration.Value > TimeSpan.FromMinutes(9));
            Assert.True(key2Expiration.Value <= TimeSpan.FromMinutes(10));

            var key3Expiration = await cache.GetExpirationAsync("key3");
            Assert.NotNull(key3Expiration);
            Assert.True(key3Expiration.Value > TimeSpan.FromMinutes(14));
            Assert.True(key3Expiration.Value <= TimeSpan.FromMinutes(15));
        }
    }

    public virtual async Task SetAllExpiration_WithNullValues_RemovesExpiration()
    {
        // Arrange
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            await cache.SetAsync("key1", 1, TimeSpan.FromMinutes(5));
            await cache.SetAsync("key2", 2, TimeSpan.FromMinutes(10));
            await cache.SetAsync("key3", 3, TimeSpan.FromMinutes(15));

            // Verify initial expirations are set
            Assert.NotNull(await cache.GetExpirationAsync("key1"));
            Assert.NotNull(await cache.GetExpirationAsync("key2"));
            Assert.NotNull(await cache.GetExpirationAsync("key3"));

            var expirations = new Dictionary<string, TimeSpan?>
            {
                { "key1", null }, // Remove expiration
                { "key2", TimeSpan.FromMinutes(20) }, // Change expiration
                { "key3", null } // Remove expiration
            };

            // Act
            await cache.SetAllExpirationAsync(expirations);

            // Assert
            Assert.Null(await cache.GetExpirationAsync("key1")); // Expiration removed
            Assert.True(await cache.ExistsAsync("key1")); // Key still exists

            var key2Expiration = await cache.GetExpirationAsync("key2");
            Assert.NotNull(key2Expiration);
            Assert.True(key2Expiration.Value > TimeSpan.FromMinutes(19));
            Assert.True(key2Expiration.Value <= TimeSpan.FromMinutes(20));

            Assert.Null(await cache.GetExpirationAsync("key3")); // Expiration removed
            Assert.True(await cache.ExistsAsync("key3")); // Key still exists
        }
    }

    public virtual async Task SetAllExpiration_WithLargeNumberOfKeys_SetsAllExpirations(int count)
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
                await cache.SetAsync(key, i);
            }

            var expirations = new Dictionary<string, TimeSpan?>();
            for (int i = 0; i < count; i++)
            {
                expirations[keys[i]] = TimeSpan.FromMinutes(i % 60 + 1);
            }

            // Act
            var sw = Stopwatch.StartNew();
            await cache.SetAllExpirationAsync(expirations);
            sw.Stop();

            _logger.LogInformation("Set All Expiration Time ({Count} keys): {Elapsed:g}", count, sw.Elapsed);

            // Assert - verify a sample of keys
            var key0Expiration = await cache.GetExpirationAsync(keys[0]);
            Assert.NotNull(key0Expiration);
            Assert.True(key0Expiration.Value <= TimeSpan.FromMinutes(1));

            var keySampleIndex = count / 2;
            var keySampleExpiration = await cache.GetExpirationAsync(keys[keySampleIndex]);
            Assert.NotNull(keySampleExpiration);
            Assert.True(keySampleExpiration.Value <= TimeSpan.FromMinutes(41));
        }
    }

    public virtual async Task SetAllExpiration_WithNonExistentKeys_HandlesGracefully()
    {
        // Arrange
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();

            await cache.SetAsync("key1", 1);
            await cache.SetAsync("key2", 2);

            var expirations = new Dictionary<string, TimeSpan?>
            {
                { "key1", TimeSpan.FromMinutes(5) },
                { "key2", TimeSpan.FromMinutes(10) },
                { "nonexistent", TimeSpan.FromMinutes(15) } // This key doesn't exist
            };

            // Act
            await cache.SetAllExpirationAsync(expirations);

            // Assert
            var key1Expiration = await cache.GetExpirationAsync("key1");
            Assert.NotNull(key1Expiration);
            Assert.True(key1Expiration.Value > TimeSpan.FromMinutes(4));
            Assert.True(key1Expiration.Value <= TimeSpan.FromMinutes(5));

            var key2Expiration = await cache.GetExpirationAsync("key2");
            Assert.NotNull(key2Expiration);
            Assert.True(key2Expiration.Value > TimeSpan.FromMinutes(9));
            Assert.True(key2Expiration.Value <= TimeSpan.FromMinutes(10));

            // Non-existent key should not be created
            Assert.False(await cache.ExistsAsync("nonexistent"));
            Assert.Null(await cache.GetExpirationAsync("nonexistent"));
        }
    }
}
