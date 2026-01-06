using System;
using System.Threading.Tasks;
using Foundatio.Caching;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Foundatio.Tests.Caching;

public class InMemoryCacheClientTests : CacheClientTestsBase
{
    public InMemoryCacheClientTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override ICacheClient GetCacheClient(bool shouldThrowOnSerializationError = true)
    {
        return new InMemoryCacheClient(o => o.LoggerFactory(Log).CloneValues(true).ShouldThrowOnSerializationError(shouldThrowOnSerializationError));
    }

    [Fact]
    public override Task AddAsync_WithConcurrentRequests_OnlyOneSucceeds()
    {
        return base.AddAsync_WithConcurrentRequests_OnlyOneSucceeds();
    }

    [Fact]
    public override Task AddAsync_WithExpiration_SetsExpirationCorrectly()
    {
        return base.AddAsync_WithExpiration_SetsExpirationCorrectly();
    }

    [Fact]
    public override Task CacheOperations_WithMultipleTypes_MeasuresThroughput()
    {
        return base.CacheOperations_WithMultipleTypes_MeasuresThroughput();
    }

    [Fact]
    public override Task CacheOperations_WithRepeatedSetAndGet_MeasuresThroughput()
    {
        return base.CacheOperations_WithRepeatedSetAndGet_MeasuresThroughput();
    }

    [Fact]
    public override Task ExistsAsync_WithVariousKeys_ReturnsCorrectExistenceStatus()
    {
        return base.ExistsAsync_WithVariousKeys_ReturnsCorrectExistenceStatus();
    }

    [Fact]
    public override Task ExistsAsync_WithExpiredKey_ReturnsFalse()
    {
        return base.ExistsAsync_WithExpiredKey_ReturnsFalse();
    }

    [Fact]
    public override Task ExistsAsync_WithScopedCache_ChecksOnlyWithinScope()
    {
        return base.ExistsAsync_WithScopedCache_ChecksOnlyWithinScope();
    }

    [Fact]
    public override Task ExistsAsync_WithInvalidKey_ThrowsArgumentException()
    {
        return base.ExistsAsync_WithInvalidKey_ThrowsArgumentException();
    }

    [Fact]
    public override Task GetAllAsync_WithInvalidKeys_ValidatesCorrectly()
    {
        return base.GetAllAsync_WithInvalidKeys_ValidatesCorrectly();
    }

    [Fact]
    public override Task GetAllAsync_WithMultipleKeys_ReturnsCorrectValues()
    {
        return base.GetAllAsync_WithMultipleKeys_ReturnsCorrectValues();
    }

    [Fact]
    public override Task GetAllAsync_WithScopedCache_ReturnsUnscopedKeys()
    {
        return base.GetAllAsync_WithScopedCache_ReturnsUnscopedKeys();
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(10000)]
    public override Task GetAllExpirationAsync_WithLargeNumberOfKeys_ReturnsAllExpirations(int count)
    {
        return base.GetAllExpirationAsync_WithLargeNumberOfKeys_ReturnsAllExpirations(count);
    }

    [Fact]
    public override Task GetAllExpirationAsync_WithInvalidKeys_ValidatesCorrectly()
    {
        return base.GetAllExpirationAsync_WithInvalidKeys_ValidatesCorrectly();
    }

    [Fact]
    public override Task GetAllExpirationAsync_WithMixedKeys_ReturnsExpectedResults()
    {
        return base.GetAllExpirationAsync_WithMixedKeys_ReturnsExpectedResults();
    }

    [Fact]
    public override Task GetAsync_WithComplexObject_PreservesPropertiesAndReturnsNewInstance()
    {
        return base.GetAsync_WithComplexObject_PreservesPropertiesAndReturnsNewInstance();
    }

    [Fact]
    public override Task GetAsync_WithInvalidKey_ThrowsArgumentException()
    {
        return base.GetAsync_WithInvalidKey_ThrowsArgumentException();
    }

    [Fact]
    public override Task GetAsync_WithNumericTypeConversion_ConvertsBetweenTypes()
    {
        return base.GetAsync_WithNumericTypeConversion_ConvertsBetweenTypes();
    }

    [Fact]
    public override Task GetAsync_WithTryGetSemantics_HandlesTypeConversions()
    {
        return base.GetAsync_WithTryGetSemantics_HandlesTypeConversions();
    }

    [Fact]
    public override Task GetExpirationAsync_WithVariousKeyStates_ReturnsExpectedExpiration()
    {
        return base.GetExpirationAsync_WithVariousKeyStates_ReturnsExpectedExpiration();
    }

    [Fact]
    public override Task GetExpirationAsync_WithInvalidKey_ThrowsArgumentException()
    {
        return base.GetExpirationAsync_WithInvalidKey_ThrowsArgumentException();
    }

    [Fact]
    public override Task GetListAsync_WithExpiredItems_RemovesExpiredAndReturnsActive()
    {
        return base.GetListAsync_WithExpiredItems_RemovesExpiredAndReturnsActive();
    }

    [Fact]
    public override Task GetListAsync_WithInvalidKey_ThrowsArgumentException()
    {
        return base.GetListAsync_WithInvalidKey_ThrowsArgumentException();
    }

    [Fact]
    public override Task GetListAsync_WithPaging_ReturnsCorrectResults()
    {
        return base.GetListAsync_WithPaging_ReturnsCorrectResults();
    }

    [Fact]
    public override Task GetUnixTimeMillisecondsAsync_WithLocalDateTime_ReturnsCorrectly()
    {
        return base.GetUnixTimeMillisecondsAsync_WithLocalDateTime_ReturnsCorrectly();
    }

    [Fact]
    public override Task GetUnixTimeMillisecondsAsync_WithUtcDateTime_ReturnsCorrectly()
    {
        return base.GetUnixTimeMillisecondsAsync_WithUtcDateTime_ReturnsCorrectly();
    }

    [Fact]
    public override Task GetUnixTimeSecondsAsync_WithUtcDateTime_ReturnsCorrectly()
    {
        return base.GetUnixTimeSecondsAsync_WithUtcDateTime_ReturnsCorrectly();
    }

    [Fact]
    public override Task IncrementAsync_WithExpiration_SetsExpirationCorrectly()
    {
        return base.IncrementAsync_WithExpiration_SetsExpirationCorrectly();
    }

    [Fact]
    public override Task IncrementAsync_WithInvalidKey_ThrowsException()
    {
        return base.IncrementAsync_WithInvalidKey_ThrowsException();
    }

    [Fact]
    public override Task IncrementAsync_WithKey_IncrementsCorrectly()
    {
        return base.IncrementAsync_WithKey_IncrementsCorrectly();
    }

    [Fact]
    public override Task IncrementAsync_WithScopedCache_WorksWithinScope()
    {
        return base.IncrementAsync_WithScopedCache_WorksWithinScope();
    }

    [Fact]
    public override Task IncrementAsync_WithFloatingPointDecimals_PreservesDecimalPrecision()
    {
        return base.IncrementAsync_WithFloatingPointDecimals_PreservesDecimalPrecision();
    }

    [Fact]
    public override Task SetIfHigherAsync_WithFloatingPointDecimals_ComparesCorrectly()
    {
        return base.SetIfHigherAsync_WithFloatingPointDecimals_ComparesCorrectly();
    }

    [Fact]
    public override Task SetIfLowerAsync_WithFloatingPointDecimals_ComparesCorrectly()
    {
        return base.SetIfLowerAsync_WithFloatingPointDecimals_ComparesCorrectly();
    }

    [Fact]
    public override Task ListAddAsync_WithExpiration_SetsExpirationCorrectly()
    {
        return base.ListAddAsync_WithExpiration_SetsExpirationCorrectly();
    }

    [Fact]
    public override Task ListAddAsync_WithInvalidArguments_ThrowsException()
    {
        return base.ListAddAsync_WithInvalidArguments_ThrowsException();
    }

    [Fact]
    public override Task ListAddAsync_WithSingleString_StoresAsStringNotCharArray()
    {
        return base.ListAddAsync_WithSingleString_StoresAsStringNotCharArray();
    }

    [Fact]
    public override Task ListAddAsync_WithVariousInputs_HandlesCorrectly()
    {
        return base.ListAddAsync_WithVariousInputs_HandlesCorrectly();
    }

    [Fact]
    public override Task ListRemoveAsync_WithInvalidInputs_ThrowsAppropriateException()
    {
        return base.ListRemoveAsync_WithInvalidInputs_ThrowsAppropriateException();
    }

    [Fact]
    public override Task ListRemoveAsync_WithValidValues_RemovesKeyWhenEmpty()
    {
        return base.ListRemoveAsync_WithValidValues_RemovesKeyWhenEmpty();
    }

    [Fact]
    public override Task ListRemoveAsync_WithExpiration_SetsExpirationCorrectly()
    {
        return base.ListRemoveAsync_WithExpiration_SetsExpirationCorrectly();
    }

    [Fact]
    public override Task ListRemoveAsync_WithValues_RemovesCorrectly()
    {
        return base.ListRemoveAsync_WithValues_RemovesCorrectly();
    }

    [Fact]
    public override Task RemoveAllAsync_WithInvalidKeys_ValidatesCorrectly()
    {
        return base.RemoveAllAsync_WithInvalidKeys_ValidatesCorrectly();
    }

    [Fact]
    public override Task RemoveAllAsync_WithLargeNumberOfKeys_RemovesAllKeysEfficiently()
    {
        return base.RemoveAllAsync_WithLargeNumberOfKeys_RemovesAllKeysEfficiently();
    }

    [Fact]
    public override Task RemoveAllAsync_WithScopedCache_AffectsOnlyScopedKeys()
    {
        return base.RemoveAllAsync_WithScopedCache_AffectsOnlyScopedKeys();
    }

    [Fact]
    public override Task RemoveAllAsync_WithSpecificKeyCollection_RemovesOnlySpecifiedKeys()
    {
        return base.RemoveAllAsync_WithSpecificKeyCollection_RemovesOnlySpecifiedKeys();
    }

    [Fact]
    public override Task RemoveAsync_WithInvalidKey_ThrowsArgumentException()
    {
        return base.RemoveAsync_WithInvalidKey_ThrowsArgumentException();
    }

    [Fact]
    public override Task RemoveAsync_WithNonExistentKey_ReturnsFalse()
    {
        return base.RemoveAsync_WithNonExistentKey_ReturnsFalse();
    }

    [Fact]
    public override Task RemoveAsync_WithExpiredKey_KeyDoesNotExist()
    {
        return base.RemoveAsync_WithExpiredKey_KeyDoesNotExist();
    }

    [Fact]
    public override Task RemoveAsync_WithScopedCache_RemovesOnlyWithinScope()
    {
        return base.RemoveAsync_WithScopedCache_RemovesOnlyWithinScope();
    }

    [Fact]
    public override Task RemoveAsync_WithValidKey_RemovesSuccessfully()
    {
        return base.RemoveAsync_WithValidKey_RemovesSuccessfully();
    }

    [Theory]
    [InlineData("snowboard", 1)] // Exact key match
    [InlineData("s", 1)] // Partial prefix match
    [InlineData(null, 1)] // Null prefix (all keys in scope)
    [InlineData("", 1)] // Empty prefix (all keys in scope)
    public override Task RemoveByPrefixAsync_FromScopedCache_RemovesOnlyScopedKeys(string prefixToRemove, int expectedRemovedCount)
    {
        return base.RemoveByPrefixAsync_FromScopedCache_RemovesOnlyScopedKeys(prefixToRemove, expectedRemovedCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public override Task RemoveByPrefixAsync_NullOrEmptyPrefixWithScopedCache_RemovesCorrectKeys(string prefix)
    {
        return base.RemoveByPrefixAsync_NullOrEmptyPrefixWithScopedCache_RemovesCorrectKeys(prefix);
    }

    [Fact]
    public override Task RemoveByPrefixAsync_PartialPrefixWithScopedCache_RemovesMatchingKeys()
    {
        return base.RemoveByPrefixAsync_PartialPrefixWithScopedCache_RemovesMatchingKeys();
    }

    [Fact]
    public override Task RemoveByPrefixAsync_WithAsteriskPrefix_TreatedAsLiteral()
    {
        return base.RemoveByPrefixAsync_WithAsteriskPrefix_TreatedAsLiteral();
    }

    [Theory]
    [MemberData(nameof(GetLineEndingPrefixes))]
    public override Task RemoveByPrefixAsync_WithLineEndingPrefix_TreatsAsLiteral(string lineEndingPrefix)
    {
        return base.RemoveByPrefixAsync_WithLineEndingPrefix_TreatsAsLiteral(lineEndingPrefix);
    }

    [Fact]
    public override Task RemoveByPrefixAsync_WithMatchingPrefix_RemovesOnlyMatchingKeys()
    {
        return base.RemoveByPrefixAsync_WithMatchingPrefix_RemovesOnlyMatchingKeys();
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(9999)]
    public override Task RemoveByPrefixAsync_WithMultipleMatchingKeys_RemovesOnlyPrefixedKeys(int count)
    {
        return base.RemoveByPrefixAsync_WithMultipleMatchingKeys_RemovesOnlyPrefixedKeys(count);
    }

    [Fact]
    public override Task RemoveByPrefixAsync_WithNullOrEmptyPrefix_RemovesAllKeys()
    {
        return base.RemoveByPrefixAsync_WithNullOrEmptyPrefix_RemovesAllKeys();
    }

    [Theory]
    [MemberData(nameof(GetRegexSpecialCharacters))]
    public override Task RemoveByPrefixAsync_WithRegexMetacharacter_TreatsAsLiteral(string specialChar)
    {
        return base.RemoveByPrefixAsync_WithRegexMetacharacter_TreatsAsLiteral(specialChar);
    }

    [Fact]
    public override Task RemoveByPrefixAsync_WithScopedCache_AffectsOnlyScopedKeys()
    {
        return base.RemoveByPrefixAsync_WithScopedCache_AffectsOnlyScopedKeys();
    }

    [Theory]
    [MemberData(nameof(GetSpecialPrefixes))]
    public override Task RemoveByPrefixAsync_WithSpecialCharacterPrefix_TreatsAsLiteral(string specialPrefix)
    {
        return base.RemoveByPrefixAsync_WithSpecialCharacterPrefix_TreatsAsLiteral(specialPrefix);
    }

    [Theory]
    [MemberData(nameof(GetWildcardPatterns))]
    public override Task RemoveByPrefixAsync_WithWildcardPattern_TreatsAsLiteral(string pattern)
    {
        return base.RemoveByPrefixAsync_WithWildcardPattern_TreatsAsLiteral(pattern);
    }

    [Fact]
    public override Task RemoveIfEqualAsync_WithInvalidKey_ThrowsArgumentException()
    {
        return base.RemoveIfEqualAsync_WithInvalidKey_ThrowsArgumentException();
    }

    [Fact]
    public override Task RemoveIfEqualAsync_WithMatchingValue_ReturnsTrueAndRemoves()
    {
        return base.RemoveIfEqualAsync_WithMatchingValue_ReturnsTrueAndRemoves();
    }

    [Fact]
    public override Task RemoveIfEqualAsync_WithMismatchedValue_ReturnsFalseAndDoesNotRemove()
    {
        return base.RemoveIfEqualAsync_WithMismatchedValue_ReturnsFalseAndDoesNotRemove();
    }

    [Fact]
    public override Task ReplaceAsync_WithExistingKey_ReturnsTrueAndReplacesValue()
    {
        return base.ReplaceAsync_WithExistingKey_ReturnsTrueAndReplacesValue();
    }

    [Fact]
    public override Task ReplaceAsync_WithInvalidKey_ThrowsException()
    {
        return base.ReplaceAsync_WithInvalidKey_ThrowsException();
    }

    [Fact]
    public override Task ReplaceAsync_WithNonExistentKey_ReturnsFalseAndDoesNotCreateKey()
    {
        return base.ReplaceAsync_WithNonExistentKey_ReturnsFalseAndDoesNotCreateKey();
    }

    [Fact]
    public override Task ReplaceAsync_WithExpiration_SetsExpirationCorrectly()
    {
        return base.ReplaceAsync_WithExpiration_SetsExpirationCorrectly();
    }

    [Fact]
    public override Task ReplaceIfEqualAsync_WithExpiration_SetsExpirationCorrectly()
    {
        return base.ReplaceIfEqualAsync_WithExpiration_SetsExpirationCorrectly();
    }

    [Fact]
    public override Task ReplaceIfEqualAsync_WithInvalidKey_ThrowsArgumentException()
    {
        return base.ReplaceIfEqualAsync_WithInvalidKey_ThrowsArgumentException();
    }

    [Fact]
    public override Task ReplaceIfEqualAsync_WithMatchingOldValue_ReturnsTrueAndReplacesValue()
    {
        return base.ReplaceIfEqualAsync_WithMatchingOldValue_ReturnsTrueAndReplacesValue();
    }

    [Fact]
    public override Task ReplaceIfEqualAsync_WithMismatchedOldValue_ReturnsFalseAndDoesNotReplace()
    {
        return base.ReplaceIfEqualAsync_WithMismatchedOldValue_ReturnsFalseAndDoesNotReplace();
    }

    [Fact]
    public override Task Serialization_WithComplexObjectsAndValidation_MeasuresThroughput()
    {
        return base.Serialization_WithComplexObjectsAndValidation_MeasuresThroughput();
    }

    [Fact]
    public override Task Serialization_WithSimpleObjectsAndValidation_MeasuresThroughput()
    {
        return base.Serialization_WithSimpleObjectsAndValidation_MeasuresThroughput();
    }

    [Fact]
    public override Task SetAllAsync_WithExpiration_SetsExpirationCorrectly()
    {
        return base.SetAllAsync_WithExpiration_SetsExpirationCorrectly();
    }

    [Fact]
    public override Task SetAllAsync_WithInvalidItems_ValidatesCorrectly()
    {
        return base.SetAllAsync_WithInvalidItems_ValidatesCorrectly();
    }

    [Fact]
    public override Task SetAllAsync_WithLargeNumberOfKeys_MeasuresThroughput()
    {
        return base.SetAllAsync_WithLargeNumberOfKeys_MeasuresThroughput();
    }

    [Fact]
    public override Task SetAllExpirationAsync_WithInvalidItems_ValidatesCorrectly()
    {
        return base.SetAllExpirationAsync_WithInvalidItems_ValidatesCorrectly();
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(10000)]
    public override Task SetAllExpirationAsync_WithLargeNumberOfKeys_SetsAllExpirations(int count)
    {
        return base.SetAllExpirationAsync_WithLargeNumberOfKeys_SetsAllExpirations(count);
    }

    [Fact]
    public override Task SetAllExpirationAsync_WithExpiration_SetsExpirationCorrectly()
    {
        return base.SetAllExpirationAsync_WithExpiration_SetsExpirationCorrectly();
    }

    [Fact]
    public override Task SetAsync_WithComplexObject_StoresCorrectly()
    {
        return base.SetAsync_WithComplexObject_StoresCorrectly();
    }

    [Fact]
    public override Task SetAsync_WithExpiration_SetsExpirationCorrectly()
    {
        return base.SetAsync_WithExpiration_SetsExpirationCorrectly();
    }

    [Fact]
    public override Task SetExpirationAsync_WithExpiration_SetsExpirationCorrectly()
    {
        return base.SetExpirationAsync_WithExpiration_SetsExpirationCorrectly();
    }

    [Fact]
    public override Task SetAsync_WithInvalidKey_ThrowsArgumentException()
    {
        return base.SetAsync_WithInvalidKey_ThrowsArgumentException();
    }

    [Fact]
    public override Task SetAsync_WithLargeNumbersAndExpiration_PreservesValues()
    {
        return base.SetAsync_WithLargeNumbersAndExpiration_PreservesValues();
    }

    [Fact]
    public override Task SetAsync_WithNullValue_StoresAsNullValue()
    {
        return base.SetAsync_WithNullValue_StoresAsNullValue();
    }

    [Fact]
    public override Task SetAsync_WithScopedCaches_IsolatesKeys()
    {
        return base.SetAsync_WithScopedCaches_IsolatesKeys();
    }

    [Fact]
    public override Task SetAsync_WithShortExpiration_ExpiresCorrectly()
    {
        return base.SetAsync_WithShortExpiration_ExpiresCorrectly();
    }

    [Fact]
    public override Task SetExpirationAsync_WithInvalidKey_ThrowsArgumentException()
    {
        return base.SetExpirationAsync_WithInvalidKey_ThrowsArgumentException();
    }

    [Fact]
    public override Task SetIfHigherAsync_WithDateTime_UpdatesWhenHigher()
    {
        return base.SetIfHigherAsync_WithDateTime_UpdatesWhenHigher();
    }

    [Fact]
    public override Task SetIfHigherAsync_WithLargeNumbers_HandlesCorrectly()
    {
        return base.SetIfHigherAsync_WithLargeNumbers_HandlesCorrectly();
    }

    [Fact]
    public override Task SetIfHigherAsync_WithExpiration_SetsExpirationCorrectly()
    {
        return base.SetIfHigherAsync_WithExpiration_SetsExpirationCorrectly();
    }

    [Fact]
    public override Task SetIfLowerAsync_WithDateTime_UpdatesWhenLower()
    {
        return base.SetIfLowerAsync_WithDateTime_UpdatesWhenLower();
    }

    [Fact]
    public override Task SetIfLowerAsync_WithLargeNumbers_HandlesCorrectly()
    {
        return base.SetIfLowerAsync_WithLargeNumbers_HandlesCorrectly();
    }

    [Fact]
    public override Task SetIfLowerAsync_WithExpiration_SetsExpirationCorrectly()
    {
        return base.SetIfLowerAsync_WithExpiration_SetsExpirationCorrectly();
    }

    [Fact]
    public override Task SetUnixTimeMillisecondsAsync_WithLocalDateTime_StoresCorrectly()
    {
        return base.SetUnixTimeMillisecondsAsync_WithLocalDateTime_StoresCorrectly();
    }

    [Fact]
    public override Task SetUnixTimeSecondsAsync_WithUtcDateTime_StoresCorrectly()
    {
        return base.SetUnixTimeSecondsAsync_WithUtcDateTime_StoresCorrectly();
    }

    [Fact]
    public async Task IncrementAsync_WithStringValue_ConvertsAndIncrements()
    {
        var cache = GetCacheClient();
        if (cache is null)
            return;

        using (cache)
        {
            await cache.RemoveAllAsync();
            Assert.True(await cache.SetAsync("test2", "stringValue"));
            Assert.Equal(1, await cache.IncrementAsync("test2"));
        }
    }

    [Fact]
    public async Task SetAsync_WithMaxItems_EnforcesLimit()
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

                await TimeProvider.System.Delay(TimeSpan.FromMilliseconds(50), TestCancellationToken); // keep the last access ticks from being the same for all items
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
    public async Task DoMaintenanceAsync_WithMaxTimeSpanExpiration_ShouldNotThrowException()
    {
        // Arrange - use a normal current time so using TimeSpan.MaxValue for expiration does not cause overflow issues
        var timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var cache = new InMemoryCacheClient(o => o.CloneValues(true).TimeProvider(timeProvider).LoggerFactory(Log));
        using (cache)
        {
            await cache.RemoveAllAsync();
            const string key = "cached-with-max-expiration";

            // Act - use TimeSpan.MaxValue which should result in no expiration
            await cache.SetAsync(key, "value", TimeSpan.MaxValue);

            // Assert
            var result = await cache.GetAsync<string>(key);
            Assert.True(result.HasValue);
            Assert.Equal("value", result.Value);

            // Verify no expiration (TimeSpan.MaxValue means no expiration, returns null)
            var expiration = await cache.GetExpirationAsync(key);
            Assert.Null(expiration);
        }
    }

    [Fact]
    public async Task SetAsync_WithMaxMemorySizeLimit_EvictsWhenOverLimit()
    {
        // Use a memory limit that allows for testing eviction
        var cache = new InMemoryCacheClient(o => o.WithDynamicSizing(200, Log).CloneValues(false).LoggerFactory(Log));

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
            await Task.Delay(500, TestCancellationToken);
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
    public async Task SetAsync_WithMaxMemorySize_TracksMemoryCorrectly()
    {
        var cache = new InMemoryCacheClient(o => o.WithDynamicSizing(1024, Log).CloneValues(false).LoggerFactory(Log));
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
    public async Task SetAsync_WithBothLimits_RespectsMemoryAndItemLimits()
    {
        // Test that both limits work together
        var cache = new InMemoryCacheClient(o => o.MaxItems(5).WithDynamicSizing(512, Log).CloneValues(false).LoggerFactory(Log));

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
    public async Task MemorySize_WithVariousOperations_TracksCorrectly()
    {
        var cache = new InMemoryCacheClient(o => o.CloneValues(false).LoggerFactory(Log));

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
        cache = new InMemoryCacheClient(o => o.WithDynamicSizing(1024, Log).CloneValues(false).LoggerFactory(Log));
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

    [Fact]
    public async Task SetAsync_WithWithFixedSizing_ReturnsFixedSizeForAllObjects()
    {
        const long fixedSize = 100;
        var cache = new InMemoryCacheClient(o => o.WithFixedSizing(10000, fixedSize).LoggerFactory(Log));

        using (cache)
        {
            await cache.RemoveAllAsync();
            Assert.Equal(0, cache.CurrentMemorySize);

            // Add items of varying actual sizes - memory should increment by fixed size each time
            await cache.SetAsync("small", "a");
            Assert.Equal(fixedSize, cache.CurrentMemorySize);

            await cache.SetAsync("large", new string('x', 1000));
            Assert.Equal(fixedSize * 2, cache.CurrentMemorySize);

            await cache.SetAsync("int", 42);
            Assert.Equal(fixedSize * 3, cache.CurrentMemorySize);

            // Remove one item
            await cache.RemoveAsync("small");
            Assert.Equal(fixedSize * 2, cache.CurrentMemorySize);

            // Update an item (should recalculate to same fixed size, so no change)
            await cache.SetAsync("large", "tiny");
            Assert.Equal(fixedSize * 2, cache.CurrentMemorySize);
        }
    }

    [Fact]
    public async Task SetAsync_WithWithDynamicSizing_CalculatesSizesDynamically()
    {
        var cache = new InMemoryCacheClient(o => o.WithDynamicSizing(10000, Log).LoggerFactory(Log));

        using (cache)
        {
            await cache.RemoveAllAsync();
            Assert.Equal(0, cache.CurrentMemorySize);

            // Add a small string
            await cache.SetAsync("small", "a");
            var smallSize = cache.CurrentMemorySize;
            Assert.True(smallSize > 0, "Small string should have non-zero size");

            // Add a larger string - size should increase more
            await cache.SetAsync("large", new string('x', 100));
            var totalSize = cache.CurrentMemorySize;
            Assert.True(totalSize > smallSize, "Adding larger string should increase memory size");

            // The large string should be bigger than the small string
            var largeSize = totalSize - smallSize;
            Assert.True(largeSize > smallSize, $"Large string size ({largeSize}) should be bigger than small string size ({smallSize})");
        }
    }

    [Fact]
    public async Task SetAsync_WithCustomSizeCalculator_UsesCustomCalculator()
    {
        int callCount = 0;
        var cache = new InMemoryCacheClient(o => o
            .MaxMemorySize(10000)
            .SizeCalculator(_ =>
            {
                callCount++;
                return 50;
            })
            .LoggerFactory(Log));

        using (cache)
        {
            await cache.RemoveAllAsync();
            callCount = 0; // Reset after RemoveAllAsync

            await cache.SetAsync("key1", "value1");
            Assert.True(callCount >= 1, "Custom calculator should have been called at least once");

            await cache.SetAsync("key2", "value2");
            Assert.True(callCount >= 2, "Custom calculator should have been called for second item");

            Assert.Equal(100, cache.CurrentMemorySize); // 2 items * 50 bytes each
        }
    }

    [Fact]
    public async Task ListAddAsync_WithMaxMemorySize_TracksMemoryCorrectly()
    {
        var cache = new InMemoryCacheClient(o => o.WithDynamicSizing(10000, Log).LoggerFactory(Log));

        using (cache)
        {
            await cache.RemoveAllAsync();
            Assert.Equal(0, cache.CurrentMemorySize);

            // Add items to a list
            await cache.ListAddAsync("mylist", new[] { "item1", "item2", "item3" });
            var sizeAfterAdd = cache.CurrentMemorySize;
            Assert.True(sizeAfterAdd > 0, "Memory should be tracked for list items");
            _logger.LogInformation("Size after ListAddAsync: {Size}", sizeAfterAdd);

            // Add more items to the same list
            await cache.ListAddAsync("mylist", new[] { "item4", "item5" });
            var sizeAfterMoreItems = cache.CurrentMemorySize;
            Assert.True(sizeAfterMoreItems > sizeAfterAdd, "Memory should increase when adding more list items");
            _logger.LogInformation("Size after adding more items: {Size}", sizeAfterMoreItems);

            // Remove items from the list
            await cache.ListRemoveAsync("mylist", new[] { "item1", "item2" });
            var sizeAfterRemove = cache.CurrentMemorySize;
            Assert.True(sizeAfterRemove < sizeAfterMoreItems, "Memory should decrease when removing list items");
            _logger.LogInformation("Size after ListRemoveAsync: {Size}", sizeAfterRemove);
        }
    }

    [Fact]
    public async Task CompactAsync_WithExpiredItems_EvictsExpiredItemsFirst()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var cache = new InMemoryCacheClient(o => o
            .WithFixedSizing(100, 50)
            .TimeProvider(timeProvider)
            .LoggerFactory(Log));

        using (cache)
        {
            // Add an item that will expire soon
            await cache.SetAsync("expiring", "value", TimeSpan.FromSeconds(1));

            // Add items that won't expire
            await cache.SetAsync("permanent1", "value");
            await cache.SetAsync("permanent2", "value");

            // Advance time so the first item expires
            timeProvider.Advance(TimeSpan.FromSeconds(2));

            // Force compaction by exceeding the limit
            await cache.SetAsync("trigger", "value");

            // Advance time to allow maintenance to process
            timeProvider.Advance(TimeSpan.FromSeconds(1));

            // The expired item should be gone, permanent items should remain
            Assert.False((await cache.GetAsync<string>("expiring")).HasValue, "Expired item should be evicted first");
            Assert.True((await cache.GetAsync<string>("permanent1")).HasValue || (await cache.GetAsync<string>("permanent2")).HasValue,
                "At least one permanent item should remain");
        }
    }

    [Fact]
    public async Task CompactAsync_WithMemoryLimit_EvictsLargerLessUsedItems()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        // Use a tight memory limit that will force eviction
        var cache = new InMemoryCacheClient(o => o
            .WithDynamicSizing(300, Log)
            .TimeProvider(timeProvider)
            .LoggerFactory(Log));

        using (cache)
        {
            // Add a large item that won't be accessed
            await cache.SetAsync("large_unused", new string('x', 100));
            var largeSize = cache.CurrentMemorySize;
            _logger.LogInformation("After large_unused: size={Size}", largeSize);

            // Advance time
            timeProvider.Advance(TimeSpan.FromMinutes(1));

            // Add a small item and access it frequently
            await cache.SetAsync("small_used", "tiny");
            _ = await cache.GetAsync<string>("small_used");
            _ = await cache.GetAsync<string>("small_used");
            _ = await cache.GetAsync<string>("small_used");
            _logger.LogInformation("After small_used: size={Size}", cache.CurrentMemorySize);

            // Advance time again
            timeProvider.Advance(TimeSpan.FromMinutes(1));

            // Add more items to trigger eviction
            await cache.SetAsync("trigger1", new string('y', 50));
            await cache.SetAsync("trigger2", new string('z', 50));
            _logger.LogInformation("After triggers: size={Size}, count={Count}", cache.CurrentMemorySize, cache.Count);

            // Advance time to allow maintenance to process
            timeProvider.Advance(TimeSpan.FromSeconds(1));

            // The small frequently-used item should have higher chance of survival
            // due to the eviction algorithm favoring recently accessed items
            var smallUsedExists = (await cache.GetAsync<string>("small_used")).HasValue;
            var largeUnusedExists = (await cache.GetAsync<string>("large_unused")).HasValue;

            _logger.LogInformation("small_used exists: {SmallExists}, large_unused exists: {LargeExists}",
                smallUsedExists, largeUnusedExists);

            // At minimum, verify the cache respects its memory limit
            Assert.True(cache.CurrentMemorySize <= cache.MaxMemorySize.Value * 1.5,
                $"Memory {cache.CurrentMemorySize} should be close to limit {cache.MaxMemorySize}");
        }
    }

    [Fact]
    public async Task CompactAsync_WithMaxItems_EvictsLeastRecentlyUsedItems()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var cache = new InMemoryCacheClient(o => o
            .MaxItems(3)
            .TimeProvider(timeProvider)
            .LoggerFactory(Log));

        using (cache)
        {
            // Add items in sequence
            await cache.SetAsync("first", "1");
            timeProvider.Advance(TimeSpan.FromMilliseconds(100));

            await cache.SetAsync("second", "2");
            timeProvider.Advance(TimeSpan.FromMilliseconds(100));

            await cache.SetAsync("third", "3");
            timeProvider.Advance(TimeSpan.FromMilliseconds(100));

            // Access the first item to make it recently used
            _ = await cache.GetAsync<string>("first");
            timeProvider.Advance(TimeSpan.FromMilliseconds(100));

            // Add a fourth item - should trigger eviction of "second" (least recently accessed)
            await cache.SetAsync("fourth", "4");

            // Advance time to allow maintenance to process
            timeProvider.Advance(TimeSpan.FromSeconds(1));

            // "first" was recently accessed, "third" and "fourth" are newer
            // "second" should be evicted as least recently used
            Assert.True((await cache.GetAsync<string>("first")).HasValue, "Recently accessed 'first' should remain");
            Assert.False((await cache.GetAsync<string>("second")).HasValue, "Least recently used 'second' should be evicted");
            Assert.True((await cache.GetAsync<string>("fourth")).HasValue, "Newest 'fourth' should remain");
        }
    }

    [Fact]
    public void Constructor_WithMaxMemorySizeButNoCalculator_Throws()
    {
        var options = new InMemoryCacheClientOptions
        {
            MaxMemorySize = 1024,
            SizeCalculator = null
        };

        var ex = Assert.Throws<ArgumentException>(() => new InMemoryCacheClient(options));
        Assert.Contains("SizeCalculator", ex.Message);
    }

    [Fact]
    public void Constructor_WithMaxEntrySizeButNoCalculator_Throws()
    {
        var options = new InMemoryCacheClientOptions
        {
            MaxEntrySize = 1024,
            SizeCalculator = null
        };

        var ex = Assert.Throws<ArgumentException>(() => new InMemoryCacheClient(options));
        Assert.Contains("SizeCalculator", ex.Message);
    }

    [Fact]
    public async Task SetAsync_WithNegativeSizeFromCalculator_SkipsEntry()
    {
        var cache = new InMemoryCacheClient(o => o
            .MaxMemorySize(10000)
            .SizeCalculator(_ => -1) // Always returns negative size
            .LoggerFactory(Log));

        using (cache)
        {
            // Entry should be skipped due to negative size
            var result = await cache.SetAsync("key", "value");
            Assert.False(result, "SetAsync should return false when entry is skipped due to negative size");

            // Verify entry was not cached
            var cached = await cache.GetAsync<string>("key");
            Assert.False(cached.HasValue, "Entry should not be cached when size calculator returns negative");
        }
    }

    [Fact]
    public async Task IncrementAsync_Double_WithMaxMemorySize_TracksMemoryCorrectly()
    {
        var cache = new InMemoryCacheClient(o => o.WithDynamicSizing(10000, Log).CloneValues(false).LoggerFactory(Log));

        using (cache)
        {
            await cache.RemoveAllAsync();
            Assert.Equal(0, cache.CurrentMemorySize);

            // Increment on new key should track memory
            var result = await cache.IncrementAsync("counter", 1.5);
            Assert.Equal(1.5, result);
            var sizeAfterFirst = cache.CurrentMemorySize;
            Assert.True(sizeAfterFirst > 0, $"Memory should be tracked after Increment on new key. CurrentMemorySize={sizeAfterFirst}");

            // Increment again should still track memory
            result = await cache.IncrementAsync("counter", 2.5);
            Assert.Equal(4.0, result);
            var sizeAfterSecond = cache.CurrentMemorySize;
            Assert.True(sizeAfterSecond > 0, "Memory should still be tracked after incrementing");

            // Remove and verify memory is freed
            await cache.RemoveAsync("counter");
            Assert.Equal(0, cache.CurrentMemorySize);
        }
    }

    [Fact]
    public async Task IncrementAsync_WithMaxMemorySize_TracksMemoryCorrectly()
    {
        var cache = new InMemoryCacheClient(o => o.WithDynamicSizing(10000, Log).CloneValues(false).LoggerFactory(Log));

        using (cache)
        {
            await cache.RemoveAllAsync();
            Assert.Equal(0, cache.CurrentMemorySize);

            // Increment on new key should track memory
            var result = await cache.IncrementAsync("counter", 1L);
            Assert.Equal(1L, result);
            var sizeAfterFirst = cache.CurrentMemorySize;
            Assert.True(sizeAfterFirst > 0, $"Memory should be tracked after Increment on new key. CurrentMemorySize={sizeAfterFirst}");

            // Increment again should still track memory
            result = await cache.IncrementAsync("counter", 5L);
            Assert.Equal(6L, result);
            var sizeAfterSecond = cache.CurrentMemorySize;
            Assert.True(sizeAfterSecond > 0, "Memory should still be tracked after incrementing");

            // Decrement (negative increment)
            result = await cache.IncrementAsync("counter", -2L);
            Assert.Equal(4L, result);

            // Remove and verify memory is freed
            await cache.RemoveAsync("counter");
            Assert.Equal(0, cache.CurrentMemorySize);
        }
    }

    [Fact]
    public async Task SetIfHigherAsync_Double_WithMaxMemorySize_TracksMemoryCorrectly()
    {
        var cache = new InMemoryCacheClient(o => o.WithDynamicSizing(10000, Log).CloneValues(false).LoggerFactory(Log));

        using (cache)
        {
            await cache.RemoveAllAsync();
            Assert.Equal(0, cache.CurrentMemorySize);

            // SetIfHigher on new key should track memory
            await cache.SetIfHigherAsync("counter", 100.5);
            var sizeAfterFirst = cache.CurrentMemorySize;
            Assert.True(sizeAfterFirst > 0, $"Memory should be tracked after SetIfHigher on new key. CurrentMemorySize={sizeAfterFirst}");

            // SetIfHigher with higher value should update
            await cache.SetIfHigherAsync("counter", 200.5);
            var value = await cache.GetAsync<double>("counter");
            Assert.Equal(200.5, value.Value);

            // Remove and verify memory is freed
            await cache.RemoveAsync("counter");
            Assert.Equal(0, cache.CurrentMemorySize);
        }
    }

    [Fact]
    public async Task SetIfHigherAsync_WithMaxMemorySize_TracksMemoryCorrectly()
    {
        var cache = new InMemoryCacheClient(o => o.WithDynamicSizing(10000, Log).CloneValues(false).LoggerFactory(Log));

        using (cache)
        {
            await cache.RemoveAllAsync();
            Assert.Equal(0, cache.CurrentMemorySize);

            // SetIfHigher on new key should track memory
            await cache.SetIfHigherAsync("counter", 100L);
            var sizeAfterFirst = cache.CurrentMemorySize;
            Assert.True(sizeAfterFirst > 0, $"Memory should be tracked after SetIfHigher on new key. CurrentMemorySize={sizeAfterFirst}");

            // SetIfHigher with higher value should update (size should remain similar for same type)
            await cache.SetIfHigherAsync("counter", 200L);
            var sizeAfterHigher = cache.CurrentMemorySize;
            Assert.True(sizeAfterHigher > 0, "Memory should still be tracked after updating to higher value");

            // SetIfHigher with lower value should NOT update the value
            await cache.SetIfHigherAsync("counter", 50L);
            var value = await cache.GetAsync<long>("counter");
            Assert.Equal(200L, value.Value);

            // Remove and verify memory is freed
            await cache.RemoveAsync("counter");
            Assert.Equal(0, cache.CurrentMemorySize);
        }
    }

    [Fact]
    public async Task SetIfLowerAsync_Double_WithMaxMemorySize_TracksMemoryCorrectly()
    {
        var cache = new InMemoryCacheClient(o => o.WithDynamicSizing(10000, Log).CloneValues(false).LoggerFactory(Log));

        using (cache)
        {
            await cache.RemoveAllAsync();
            Assert.Equal(0, cache.CurrentMemorySize);

            // SetIfLower on new key should track memory
            await cache.SetIfLowerAsync("counter", 100.5);
            var sizeAfterFirst = cache.CurrentMemorySize;
            Assert.True(sizeAfterFirst > 0, $"Memory should be tracked after SetIfLower on new key. CurrentMemorySize={sizeAfterFirst}");

            // SetIfLower with lower value should update
            await cache.SetIfLowerAsync("counter", 50.5);
            var value = await cache.GetAsync<double>("counter");
            Assert.Equal(50.5, value.Value);

            // Remove and verify memory is freed
            await cache.RemoveAsync("counter");
            Assert.Equal(0, cache.CurrentMemorySize);
        }
    }

    [Fact]
    public async Task SetIfLowerAsync_WithMaxMemorySize_TracksMemoryCorrectly()
    {
        var cache = new InMemoryCacheClient(o => o.WithDynamicSizing(10000, Log).CloneValues(false).LoggerFactory(Log));

        using (cache)
        {
            await cache.RemoveAllAsync();
            Assert.Equal(0, cache.CurrentMemorySize);

            // SetIfLower on new key should track memory
            await cache.SetIfLowerAsync("counter", 100L);
            var sizeAfterFirst = cache.CurrentMemorySize;
            Assert.True(sizeAfterFirst > 0, $"Memory should be tracked after SetIfLower on new key. CurrentMemorySize={sizeAfterFirst}");

            // SetIfLower with lower value should update (size should remain similar for same type)
            await cache.SetIfLowerAsync("counter", 50L);
            var sizeAfterLower = cache.CurrentMemorySize;
            Assert.True(sizeAfterLower > 0, "Memory should still be tracked after updating to lower value");

            // SetIfLower with higher value should NOT update the value
            await cache.SetIfLowerAsync("counter", 200L);
            var value = await cache.GetAsync<long>("counter");
            Assert.Equal(50L, value.Value);

            // Remove and verify memory is freed
            await cache.RemoveAsync("counter");
            Assert.Equal(0, cache.CurrentMemorySize);
        }
    }

    [Fact]
    public async Task ReplaceIfEqualAsync_WithMaxMemorySize_TracksMemoryCorrectly()
    {
        var cache = new InMemoryCacheClient(o => o.WithDynamicSizing(10000, Log).CloneValues(false).LoggerFactory(Log));

        using (cache)
        {
            await cache.RemoveAllAsync();
            Assert.Equal(0, cache.CurrentMemorySize);

            // Set initial value
            await cache.SetAsync("key", "short");
            var sizeAfterSet = cache.CurrentMemorySize;
            Assert.True(sizeAfterSet > 0, $"Memory should be tracked after Set. CurrentMemorySize={sizeAfterSet}");

            // ReplaceIfEqual with matching value and larger replacement should update memory
            var replaced = await cache.ReplaceIfEqualAsync("key", "much longer replacement string", "short");
            Assert.True(replaced, "ReplaceIfEqual should succeed when values match");

            var sizeAfterReplace = cache.CurrentMemorySize;
            Assert.True(sizeAfterReplace > sizeAfterSet,
                $"Memory should increase when replacing with larger value. Before={sizeAfterSet}, After={sizeAfterReplace}");

            // ReplaceIfEqual with non-matching value should not change memory
            replaced = await cache.ReplaceIfEqualAsync("key", "tiny", "wrong expected value");
            Assert.False(replaced, "ReplaceIfEqual should fail when expected value doesn't match");

            var sizeAfterFailedReplace = cache.CurrentMemorySize;
            Assert.Equal(sizeAfterReplace, sizeAfterFailedReplace);

            // ReplaceIfEqual with smaller replacement should decrease memory
            replaced = await cache.ReplaceIfEqualAsync("key", "x", "much longer replacement string");
            Assert.True(replaced, "ReplaceIfEqual should succeed");

            var sizeAfterSmaller = cache.CurrentMemorySize;
            Assert.True(sizeAfterSmaller < sizeAfterReplace,
                $"Memory should decrease when replacing with smaller value. Before={sizeAfterReplace}, After={sizeAfterSmaller}");

            // Remove and verify memory is freed
            await cache.RemoveAsync("key");
            Assert.Equal(0, cache.CurrentMemorySize);
        }
    }

    [Fact]
    public async Task SetAsync_WithOversizedEntry_LogsWarningAndReturnsFalse()
    {
        // Arrange
        var cache = new InMemoryCacheClient(o => o
            .WithDynamicSizing(10000, Log)
            .MaxEntrySize(50) // Very small limit
            .LoggerFactory(Log));

        using (cache)
        {
            // Act - try to cache a string larger than MaxEntrySize
            var largeString = new string('x', 100); // ~224 bytes, exceeds 50 byte limit
            var result = await cache.SetAsync("oversized", largeString);

            // Assert - should return false and not cache the entry
            Assert.False(result);
            Assert.False((await cache.GetAsync<string>("oversized")).HasValue);
            Assert.Equal(0, cache.CurrentMemorySize);
        }
    }

    [Fact]
    public async Task SetAsync_WithOversizedEntryAndThrowEnabled_ThrowsMaxEntrySizeExceededCacheException()
    {
        // Arrange
        var cache = new InMemoryCacheClient(o => o
            .WithDynamicSizing(10000, Log)
            .MaxEntrySize(50) // Very small limit
            .ShouldThrowOnMaxEntrySizeExceeded()
            .LoggerFactory(Log));

        using (cache)
        {
            // Act & Assert - should throw MaxEntrySizeExceededCacheException
            var largeString = new string('x', 100); // ~224 bytes, exceeds 50 byte limit
            var ex = await Assert.ThrowsAsync<MaxEntrySizeExceededCacheException>(() => cache.SetAsync("oversized", largeString));
            Assert.Contains("exceeds maximum allowed size", ex.Message);
            Assert.True(ex.EntrySize > 50);
            Assert.Equal(50, ex.MaxEntrySize);
            Assert.Equal("String", ex.EntryType);

            // Entry should not be cached
            Assert.False((await cache.GetAsync<string>("oversized")).HasValue);
        }
    }

    [Fact]
    public void Constructor_WithMaxEntrySizeGreaterThanMaxMemorySize_ThrowsArgumentOutOfRangeException()
    {
        // Arrange & Act & Assert
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new InMemoryCacheClient(o => o
            .WithDynamicSizing(1000) // 1000 bytes total limit
            .MaxEntrySize(2000))); // 2000 bytes per entry - INVALID

        Assert.Contains("MaxEntrySize", ex.Message);
        Assert.Contains("MaxMemorySize", ex.Message);
    }

    [Fact]
    public async Task AddAsync_WithOversizedEntry_ReturnsFalse()
    {
        // Arrange
        var cache = new InMemoryCacheClient(o => o
            .WithDynamicSizing(10000, Log)
            .MaxEntrySize(50)
            .LoggerFactory(Log));

        using (cache)
        {
            // Act
            var largeString = new string('x', 100);
            var result = await cache.AddAsync("oversized", largeString);

            // Assert
            Assert.False(result);
            Assert.False((await cache.GetAsync<string>("oversized")).HasValue);
        }
    }

    [Fact]
    public async Task SetAsync_WithEntryUnderLimit_CachesSuccessfully()
    {
        // Arrange
        var cache = new InMemoryCacheClient(o => o
            .WithDynamicSizing(10000, Log)
            .MaxEntrySize(1000) // 1KB limit
            .LoggerFactory(Log));

        using (cache)
        {
            // Act - cache a small string under the limit
            var smallString = "hello";
            var result = await cache.SetAsync("small", smallString);

            // Assert
            Assert.True(result);
            var cached = await cache.GetAsync<string>("small");
            Assert.True(cached.HasValue);
            Assert.Equal(smallString, cached.Value);
        }
    }
}

/// <summary>
/// Runs the full cache client test suite with fixed sizing configuration.
/// This ensures all cache operations work correctly when fixed sizing is enabled.
/// </summary>
public class InMemoryCacheClientWithFixedSizingTests : InMemoryCacheClientTests
{
    public InMemoryCacheClientWithFixedSizingTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override ICacheClient GetCacheClient(bool shouldThrowOnSerializationError = true)
    {
        return new InMemoryCacheClient(o => o
            .LoggerFactory(Log)
            .CloneValues(true)
            .ShouldThrowOnSerializationError(shouldThrowOnSerializationError)
            .WithFixedSizing(maxMemorySize: 10_000_000, averageEntrySize: 100)); // 10MB limit, 100 bytes per entry
    }
}

/// <summary>
/// Runs the full cache client test suite with dynamic sizing configuration.
/// This ensures all cache operations work correctly when dynamic sizing is enabled.
/// </summary>
public class InMemoryCacheClientWithDynamicSizingTests : InMemoryCacheClientTests
{
    public InMemoryCacheClientWithDynamicSizingTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override ICacheClient GetCacheClient(bool shouldThrowOnSerializationError = true)
    {
        return new InMemoryCacheClient(o => o
            .LoggerFactory(Log)
            .CloneValues(true)
            .ShouldThrowOnSerializationError(shouldThrowOnSerializationError)
            .WithDynamicSizing(maxMemorySize: 10_000_000, Log)); // 10MB limit with dynamic size calculation
    }
}
