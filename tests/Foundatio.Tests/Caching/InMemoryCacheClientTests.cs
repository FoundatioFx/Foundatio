using System;
using System.Threading.Tasks;
using Foundatio.Caching;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Caching;

public class InMemoryCacheClientTests : CacheClientTestsBase
{
    public InMemoryCacheClientTests(ITestOutputHelper output) : base(output) { }

    protected override ICacheClient GetCacheClient(bool shouldThrowOnSerializationError = true)
    {
        return new InMemoryCacheClient(o => o.LoggerFactory(Log).CloneValues(true).ShouldThrowOnSerializationError(shouldThrowOnSerializationError));
    }

    [Fact]
    public override Task CanGetAllAsync()
    {
        return base.CanGetAllAsync();
    }

    [Fact]
    public override Task CanGetAllWithOverlapAsync()
    {
        return base.CanGetAllWithOverlapAsync();
    }

    [Fact]
    public override Task CanSetAsync()
    {
        return base.CanSetAsync();
    }

    [Fact]
    public override Task CanSetAndGetValueAsync()
    {
        return base.CanSetAndGetValueAsync();
    }

    [Fact]
    public override Task CanAddAsync()
    {
        return base.CanAddAsync();
    }

    [Fact]
    public override Task CanAddConcurrentlyAsync()
    {
        return base.CanAddConcurrentlyAsync();
    }

    [Fact]
    public override Task CanGetAsync()
    {
        return base.CanGetAsync();
    }

    [Fact]
    public override Task CanTryGetAsync()
    {
        return base.CanTryGetAsync();
    }

    [Fact]
    public override Task CanUseScopedCachesAsync()
    {
        return base.CanUseScopedCachesAsync();
    }

    [Fact]
    public override Task CanSetAndGetObjectAsync()
    {
        return base.CanSetAndGetObjectAsync();
    }

    [Fact]
    public override Task CanRemoveAllAsync()
    {
        return base.CanRemoveAllAsync();
    }

    [Fact]
    public override Task CanRemoveAllKeysAsync()
    {
        return base.CanRemoveAllKeysAsync();
    }

    [Fact]
    public override Task CanRemoveByPrefixAsync()
    {
        return base.CanRemoveByPrefixAsync();
    }

    [Theory]
    [MemberData(nameof(GetRegexSpecialCharacters))]
    public override Task CanRemoveByPrefixWithRegexCharactersAsync(string specialChar)
    {
        return base.CanRemoveByPrefixWithRegexCharactersAsync(specialChar);
    }

    [Theory]
    [MemberData(nameof(GetWildcardPatterns))]
    public override Task CanRemoveByPrefixWithWildcardPatternsAsync(string pattern)
    {
        return base.CanRemoveByPrefixWithWildcardPatternsAsync(pattern);
    }

    [Fact]
    public override Task CanRemoveByPrefixWithDoubleAsteriskAsync()
    {
        return base.CanRemoveByPrefixWithDoubleAsteriskAsync();
    }

    [Theory]
    [MemberData(nameof(GetSpecialPrefixes))]
    public override Task CanRemoveByPrefixWithSpecialCharactersAsync(string specialPrefix)
    {
        return base.CanRemoveByPrefixWithSpecialCharactersAsync(specialPrefix);
    }

    [Fact]
    public override Task CanRemoveByPrefixWithNullAsync()
    {
        return base.CanRemoveByPrefixWithNullAsync();
    }

    [Fact]
    public override Task CanRemoveByPrefixWithEmptyStringAsync()
    {
        return base.CanRemoveByPrefixWithEmptyStringAsync();
    }

    [Theory]
    [MemberData(nameof(GetWhitespaceOnlyPrefixes))]
    public override Task CanRemoveByPrefixWithWhitespaceAsync(string whitespacePrefix)
    {
        return base.CanRemoveByPrefixWithWhitespaceAsync(whitespacePrefix);
    }

    [Theory]
    [MemberData(nameof(GetLineEndingPrefixes))]
    public override Task CanRemoveByPrefixWithLineEndingsAsync(string lineEndingPrefix)
    {
        return base.CanRemoveByPrefixWithLineEndingsAsync(lineEndingPrefix);
    }

    [Fact]
    public override Task CanRemoveByPrefixWithScopedCachesAsync()
    {
        return base.CanRemoveByPrefixWithScopedCachesAsync();
    }

    [Theory]
    [InlineData(50)]
    [InlineData(500)]
    public override Task CanRemoveByPrefixMultipleEntriesAsync(int count)
    {
        return base.CanRemoveByPrefixMultipleEntriesAsync(count);
    }

    [Fact]
    public override Task CanSetExpirationAsync()
    {
        return base.CanSetExpirationAsync();
    }

    [Fact]
    public override Task CanSetMinMaxExpirationAsync()
    {
        return base.CanSetMinMaxExpirationAsync();
    }

    [Fact]
    public override Task CanIncrementAsync()
    {
        return base.CanIncrementAsync();
    }

    [Fact]
    public override Task CanIncrementAndExpireAsync()
    {
        return base.CanIncrementAndExpireAsync();
    }

    [Fact]
    public override Task SetAllShouldExpireAsync()
    {
        return base.SetAllShouldExpireAsync();
    }

    [Fact]
    public override Task CanReplaceIfEqual()
    {
        return base.CanReplaceIfEqual();
    }

    [Fact]
    public override Task CanRemoveIfEqual()
    {
        return base.CanRemoveIfEqual();
    }

    [Fact]
    public override Task CanGetAndSetDateTimeAsync()
    {
        return base.CanGetAndSetDateTimeAsync();
    }

    [Fact]
    public override Task CanRoundTripLargeNumbersAsync()
    {
        return base.CanRoundTripLargeNumbersAsync();
    }

    [Fact]
    public override Task CanRoundTripLargeNumbersWithExpirationAsync()
    {
        return base.CanRoundTripLargeNumbersWithExpirationAsync();
    }

    [Fact]
    public override Task CanManageListsAsync()
    {
        return base.CanManageListsAsync();
    }

    [Fact]
    public override Task CanManageListsWithNullItemsAsync()
    {
        return base.CanManageListsWithNullItemsAsync();
    }

    [Fact]
    public override Task CanManageStringListsAsync()
    {
        return base.CanManageStringListsAsync();
    }

    [Fact]
    public override Task CanManageListPagingAsync()
    {
        return base.CanManageListPagingAsync();
    }

    [Fact]
    public override Task CanManageGetListExpirationAsync()
    {
        return base.CanManageGetListExpirationAsync();
    }

    [Fact]
    public override Task CanManageListAddExpirationAsync()
    {
        return base.CanManageListAddExpirationAsync();
    }

    [Fact]
    public override Task CanManageListRemoveExpirationAsync()
    {
        return base.CanManageListRemoveExpirationAsync();
    }

    [Fact]
    public override Task MeasureThroughputAsync()
    {
        return base.MeasureThroughputAsync();
    }

    [Fact]
    public override Task MeasureSerializerSimpleThroughputAsync()
    {
        return base.MeasureSerializerSimpleThroughputAsync();
    }

    [Fact]
    public override Task MeasureSerializerComplexThroughputAsync()
    {
        return base.MeasureSerializerComplexThroughputAsync();
    }

    [Fact]
    public async Task CanSetMaxItems()
    {
        // run in a tight loop so that the code is warmed up and we can catch timing issues
        for (int x = 0; x < 5; x++)
        {
            var cache = new InMemoryCacheClient(o => o.MaxItems(10).CloneValues(true));

            using (cache)
            {
                await cache.RemoveAllAsync();

                for (int i = 0; i < cache.MaxItems; i++)
                    await cache.SetAsync("test" + i, i);

                _logger.LogTrace("Keys: {Keys}", String.Join(",", cache.Keys));
                Assert.Equal(10, cache.Count);
                await cache.SetAsync("next", 1);
                _logger.LogTrace("Keys: {Keys}", String.Join(",", cache.Keys));
                Assert.Equal(10, cache.Count);
                Assert.False((await cache.GetAsync<int>("test0")).HasValue);
                Assert.Equal(1, cache.Misses);
                await TimeProvider.System.Delay(TimeSpan.FromMilliseconds(50)); // keep the last access ticks from being the same for all items
                Assert.NotNull(await cache.GetAsync<int?>("test1"));
                Assert.Equal(1, cache.Hits);
                await cache.SetAsync("next2", 2);
                _logger.LogTrace("Keys: {Keys}", String.Join(",", cache.Keys));
                Assert.False((await cache.GetAsync<int>("test2")).HasValue);
                Assert.Equal(2, cache.Misses);
                Assert.True((await cache.GetAsync<int>("test1")).HasValue);
                Assert.Equal(2, cache.Misses);
            }
        }
    }

    [Fact]
    public async Task CanSetMaxMemorySize()
    {
        // Use a memory limit that allows for testing eviction
        var cache = new InMemoryCacheClient(o => o.MaxMemorySize(200).CloneValues(false));

        using (cache)
        {
            await cache.RemoveAllAsync();
            Assert.Equal(0, cache.CurrentMemorySize);

            // Add some entries with known sizes
            await cache.SetAsync("small1", "test"); // ~32 bytes
            _logger.LogInformation($"After adding 'test': CurrentMemorySize={cache.CurrentMemorySize}");
            Assert.True(cache.CurrentMemorySize > 0, $"Expected memory size > 0, but was {cache.CurrentMemorySize}");
            var sizeAfterFirst = cache.CurrentMemorySize;

            await cache.SetAsync("small2", "test2"); // ~34 bytes
            _logger.LogInformation($"After adding 'test2': CurrentMemorySize={cache.CurrentMemorySize}");
            Assert.True(cache.CurrentMemorySize > sizeAfterFirst, $"Expected memory size > {sizeAfterFirst}, but was {cache.CurrentMemorySize}");

            // Add medium strings to approach the limit
            await cache.SetAsync("medium1", new string('a', 50)); // ~124 bytes
            await cache.SetAsync("medium2", new string('b', 50)); // ~124 bytes  
            _logger.LogInformation($"After adding medium strings: CurrentMemorySize={cache.CurrentMemorySize}");

            // Add one more item that should trigger eviction
            await cache.SetAsync("final", "trigger"); // Should trigger cleanup
            _logger.LogInformation($"After adding final item: CurrentMemorySize={cache.CurrentMemorySize}");

            // The cache should respect the memory limit (allowing some tolerance for async cleanup)
            // Give it a moment for async maintenance to run
            await Task.Delay(500);
            _logger.LogInformation($"After delay: CurrentMemorySize={cache.CurrentMemorySize}");
            
            Assert.True(cache.CurrentMemorySize <= cache.MaxMemorySize.Value * 1.5, 
                $"Memory size {cache.CurrentMemorySize} should be close to or below limit {cache.MaxMemorySize} (allowing 50% tolerance for async cleanup)");
            
            // At least some items should still be accessible
            var hasAnyItems = (await cache.GetAsync<string>("small1")).HasValue ||
                             (await cache.GetAsync<string>("small2")).HasValue ||
                             (await cache.GetAsync<string>("medium1")).HasValue ||
                             (await cache.GetAsync<string>("medium2")).HasValue ||
                             (await cache.GetAsync<string>("final")).HasValue;
            
            Assert.True(hasAnyItems, "At least some items should remain in cache");
        }
    }

    [Fact]
    public async Task DebugMemoryTracking()
    {
        var cache = new InMemoryCacheClient(o => o.MaxMemorySize(1024).CloneValues(false));
        using (cache)
        {
            _logger.LogInformation($"Initial state: MaxMemorySize={cache.MaxMemorySize}, CurrentMemorySize={cache.CurrentMemorySize}");
            
            await cache.SetAsync("key1", "value1");
            _logger.LogInformation($"After set key1: CurrentMemorySize={cache.CurrentMemorySize}");
            
            // Verify the entry was actually added
            var result = await cache.GetAsync<string>("key1");
            _logger.LogInformation($"Retrieved key1: HasValue={result.HasValue}, Value='{result.Value}'");
            
            Assert.True(result.HasValue, "Key should exist in cache");
            
            // Only assert memory tracking if the cache is configured for it
            if (cache.MaxMemorySize.HasValue)
            {
                Assert.True(cache.CurrentMemorySize > 0, $"Memory should be tracked when MaxMemorySize is set. CurrentMemorySize={cache.CurrentMemorySize}");
            }
        }
    }

    [Fact]
    public async Task MaxMemorySizeWorksWithMaxItems()
    {
        // Test that both limits work together
        var cache = new InMemoryCacheClient(o => o.MaxItems(5).MaxMemorySize(512).CloneValues(false));

        using (cache)
        {
            await cache.RemoveAllAsync();

            // Add items that should trigger memory limit before item limit
            var mediumString = new string('a', 100); // ~224 bytes each
            
            await cache.SetAsync("item1", mediumString);
            await cache.SetAsync("item2", mediumString);
            await cache.SetAsync("item3", mediumString); // Should be close to or over 512 bytes total

            // Verify limits are respected
            Assert.True(cache.Count <= cache.MaxItems);
            Assert.True(cache.CurrentMemorySize <= cache.MaxMemorySize || cache.Count == 0);
        }
    }

    [Fact]
    public async Task MemorySizeIsTrackedCorrectly()
    {
        var cache = new InMemoryCacheClient(o => o.MaxMemorySize(null).CloneValues(false));

        using (cache)
        {
            await cache.RemoveAllAsync();
            // Memory tracking should only occur when MaxMemorySize is set
            Assert.Null(cache.MaxMemorySize);
            
            await cache.SetAsync("key1", "value1");
            // When MaxMemorySize is null, CurrentMemorySize should be 0
            Assert.Equal(0, cache.CurrentMemorySize);
        }
        
        // Test with MaxMemorySize set
        cache = new InMemoryCacheClient(o => o.MaxMemorySize(1024).CloneValues(false));
        using (cache)
        {
            await cache.RemoveAllAsync();
            Assert.Equal(0, cache.CurrentMemorySize);

            // Test adding items
            await cache.SetAsync("key1", "value1");
            var sizeAfterAdd = cache.CurrentMemorySize;
            Assert.True(sizeAfterAdd > 0, $"Expected memory size > 0 after add, but was {sizeAfterAdd}");

            // Test updating items
            await cache.SetAsync("key1", "longer_value1");
            var sizeAfterUpdate = cache.CurrentMemorySize;
            Assert.True(sizeAfterUpdate != sizeAfterAdd, $"Expected memory size to change after update, was {sizeAfterAdd}, now {sizeAfterUpdate}");

            // Test removing items
            await cache.RemoveAsync("key1");
            Assert.Equal(0, cache.CurrentMemorySize);

            // Test removing all
            await cache.SetAsync("key1", "value1");
            await cache.SetAsync("key2", "value2");
            Assert.True(cache.CurrentMemorySize > 0);
            
            await cache.RemoveAllAsync();
            Assert.Equal(0, cache.CurrentMemorySize);
        }
    }
}
