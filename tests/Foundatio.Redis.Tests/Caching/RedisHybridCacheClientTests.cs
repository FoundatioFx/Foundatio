using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Redis.Tests.Extensions;
using Foundatio.Tests.Caching;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Foundatio.Redis.Tests.Caching;

public class RedisHybridCacheClientTests : HybridCacheClientTestBase, IAsyncLifetime
{
    public RedisHybridCacheClientTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override ICacheClient GetCacheClient(bool shouldThrowOnSerializationError = true)
    {
        return new RedisHybridCacheClient(o => o
                .ConnectionMultiplexer(SharedConnection.GetMuxer(Log))
                .LoggerFactory(Log).ShouldThrowOnSerializationError(shouldThrowOnSerializationError),
            localConfig => localConfig
                .CloneValues(true)
                .ShouldThrowOnSerializationError(shouldThrowOnSerializationError));
    }

    [Fact]
    public override Task AddAsync_WithConcurrentRequests_OnlyOneSucceeds()
    {
        return base.AddAsync_WithConcurrentRequests_OnlyOneSucceeds();
    }

    [Fact]
    public override Task AddAsync_WithInvalidKey_ThrowsArgumentException()
    {
        return base.AddAsync_WithInvalidKey_ThrowsArgumentException();
    }

    [Fact(Skip = "TODO: Look into this as the local cache client maintenance never schedules for expiration")]
    public override Task AddAsync_WithExpiration_ExpiresRemoteItems()
    {
        return base.AddAsync_WithExpiration_ExpiresRemoteItems();
    }

    [Fact]
    public override Task AddAsync_WithExpiration_SetsExpirationCorrectly()
    {
        return base.AddAsync_WithExpiration_SetsExpirationCorrectly();
    }

    [Theory]
    [InlineData("user:profile")]
    [InlineData("   ")]
    public override Task AddAsync_WhenKeyDoesNotExist_AddsValueAndReturnsTrue(string cacheKey)
    {
        return base.AddAsync_WhenKeyDoesNotExist_AddsValueAndReturnsTrue(cacheKey);
    }

    [Fact(Skip = "Performance Test")]
    public override Task CacheOperations_WithMultipleTypes_MeasuresThroughput()
    {
        return base.CacheOperations_WithMultipleTypes_MeasuresThroughput();
    }

    [Fact(Skip = "Performance Test")]
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
    public override Task ExistsAsync_WithExpiredKey_ReturnsFalse() => base.ExistsAsync_WithExpiredKey_ReturnsFalse();

    [Fact]
    public override Task ExistsAsync_WithScopedCache_ChecksOnlyWithinScope() => base.ExistsAsync_WithScopedCache_ChecksOnlyWithinScope();

    [Fact]
    public override Task ExistsAsync_WithInvalidKey_ThrowsArgumentException() => base.ExistsAsync_WithInvalidKey_ThrowsArgumentException();

    [Fact]
    public override Task ExistsAsync_WithLocalCache_ChecksLocalCacheFirst()
    {
        return base.ExistsAsync_WithLocalCache_ChecksLocalCacheFirst();
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
    public override Task GetAllAsync_WithMultipleKeys_UsesHybridCache()
    {
        return base.GetAllAsync_WithMultipleKeys_UsesHybridCache();
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
    public override Task GetExpirationAsync_WithLocalCache_ChecksLocalCacheFirst()
    {
        return base.GetExpirationAsync_WithLocalCache_ChecksLocalCacheFirst();
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
    public override Task IncrementAsync_WithMultipleInstances_InvalidatesOtherClientLocalCache()
    {
        return base.IncrementAsync_WithMultipleInstances_InvalidatesOtherClientLocalCache();
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
    public override Task ListAddAsync_WithMultipleInstances_WorksCorrectly()
    {
        return base.ListAddAsync_WithMultipleInstances_WorksCorrectly();
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
    public override Task ListRemoveAsync_WithValues_RemovesCorrectly()
    {
        return base.ListRemoveAsync_WithValues_RemovesCorrectly();
    }

    [Fact]
    public override Task ListRemoveAsync_WithMultipleItems_RemovesCorrectly()
    {
        return base.ListRemoveAsync_WithMultipleItems_RemovesCorrectly();
    }

    [Fact]
    public override Task ListRemoveAsync_WithNonExistentValues_DoesNotPublishInvalidation()
    {
        return base.ListRemoveAsync_WithNonExistentValues_DoesNotPublishInvalidation();
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
    public override Task RemoveAllAsync_WithLocalCache_InvalidatesLocalCache()
    {
        return base.RemoveAllAsync_WithLocalCache_InvalidatesLocalCache();
    }

    [Fact]
    public override Task RemoveAllAsync_WithNonExistentKeys_DoesNotPublishInvalidation()
    {
        return base.RemoveAllAsync_WithNonExistentKeys_DoesNotPublishInvalidation();
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
    public override Task RemoveAsync_WithNonExistentKey_DoesNotPublishInvalidation()
    {
        return base.RemoveAsync_WithNonExistentKey_DoesNotPublishInvalidation();
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
    public override Task RemoveByPrefixAsync_WithLocalCache_InvalidatesLocalCache()
    {
        return base.RemoveByPrefixAsync_WithLocalCache_InvalidatesLocalCache();
    }

    [Fact]
    public override Task RemoveByPrefixAsync_WithMatchingPrefix_RemovesOnlyMatchingKeys()
    {
        return base.RemoveByPrefixAsync_WithMatchingPrefix_RemovesOnlyMatchingKeys();
    }

    [Fact]
    public override Task RemoveByPrefixAsync_WithNonMatchingPrefix_DoesNotPublishInvalidation()
    {
        return base.RemoveByPrefixAsync_WithNonMatchingPrefix_DoesNotPublishInvalidation();
    }

    [Theory]
    [InlineData(50)]
    [InlineData(500)]
    [InlineData(5000)]
    [InlineData(50000)]
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
    public override Task RemoveIfEqualAsync_WithMultipleInstances_InvalidatesOtherClientLocalCache()
    {
        return base.RemoveIfEqualAsync_WithMultipleInstances_InvalidatesOtherClientLocalCache();
    }

    [Fact]
    public override Task RemoveIfEqualAsync_WithNonMatchingValue_DoesNotPublishInvalidation()
    {
        return base.RemoveIfEqualAsync_WithNonMatchingValue_DoesNotPublishInvalidation();
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
    public override Task ReplaceIfEqualAsync_WithMultipleInstances_InvalidatesOtherClientLocalCache()
    {
        return base.ReplaceIfEqualAsync_WithMultipleInstances_InvalidatesOtherClientLocalCache();
    }

    [Fact(Skip = "Performance Test")]
    public override Task Serialization_WithComplexObjectsAndValidation_MeasuresThroughput()
    {
        return base.Serialization_WithComplexObjectsAndValidation_MeasuresThroughput();
    }

    [Fact(Skip = "Performance Test")]
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
    public override Task SetAsync_WithMultipleInstances_UsesLocalCache()
    {
        return base.SetAsync_WithMultipleInstances_UsesLocalCache();
    }

    [Fact]
    public override Task SetAsync_WithMultipleInstances_InvalidatesOtherClientLocalCache()
    {
        return base.SetAsync_WithMultipleInstances_InvalidatesOtherClientLocalCache();
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
    public override Task SetExpirationAsync_WithExpiration_SetsExpirationCorrectly()
    {
        return base.SetExpirationAsync_WithExpiration_SetsExpirationCorrectly();
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

    public ValueTask InitializeAsync()
    {
        _logger.LogDebug("Initializing");
        var muxer = SharedConnection.GetMuxer(Log);
        return new ValueTask(muxer.FlushAllAsync());
    }

    public ValueTask DisposeAsync()
    {
        _logger.LogDebug("Disposing");
        Dispose();

        return ValueTask.CompletedTask;
    }
}
