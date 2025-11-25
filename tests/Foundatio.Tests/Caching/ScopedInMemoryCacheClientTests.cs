using System.Threading.Tasks;
using Foundatio.Caching;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Caching;

public class ScopedInMemoryCacheClientTests : CacheClientTestsBase
{
    public ScopedInMemoryCacheClientTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override ICacheClient GetCacheClient(bool shouldThrowOnSerializationError = true)
    {
        return new ScopedCacheClient(new InMemoryCacheClient(o => o.LoggerFactory(Log).CloneValues(true).ShouldThrowOnSerializationError(shouldThrowOnSerializationError)), "scoped");
    }

    [Fact]
    public override Task AddAsync_WithConcurrentRequests_OnlyOneSucceeds()
    {
        return base.AddAsync_WithConcurrentRequests_OnlyOneSucceeds();
    }

    [Fact]
    public override Task AddAsync_WithEmptyKey_ThrowsArgumentException()
    {
        return base.AddAsync_WithEmptyKey_ThrowsArgumentException();
    }

    [Theory]
    [InlineData("user:profile")]
    [InlineData("   ")]
    public override Task AddAsync_WithExistingKey_ReturnsFalseAndPreservesValue(string cacheKey)
    {
        return base.AddAsync_WithExistingKey_ReturnsFalseAndPreservesValue(cacheKey);
    }

    [Fact]
    public override Task AddAsync_WithNestedKeyUsingSeparator_StoresCorrectly()
    {
        return base.AddAsync_WithNestedKeyUsingSeparator_StoresCorrectly();
    }

    [Theory]
    [InlineData("user:profile")]
    [InlineData("   ")]
    public override Task AddAsync_WithValidKey_ReturnsTrue(string cacheKey)
    {
        return base.AddAsync_WithValidKey_ReturnsTrue(cacheKey);
    }

    [Fact]
    public override Task AddAsync_WithNullKey_ThrowsArgumentNullException()
    {
        return base.AddAsync_WithNullKey_ThrowsArgumentNullException();
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
    public override Task ExistsAsync_AfterKeyExpires_ReturnsFalse()
    {
        return base.ExistsAsync_AfterKeyExpires_ReturnsFalse();
    }

    [Fact]
    public override Task ExistsAsync_WithDifferentCasedKeys_ChecksExactMatch()
    {
        return base.ExistsAsync_WithDifferentCasedKeys_ChecksExactMatch();
    }

    [Fact]
    public override Task ExistsAsync_WithEmptyKey_ThrowsArgumentException()
    {
        return base.ExistsAsync_WithEmptyKey_ThrowsArgumentException();
    }

    [Theory]
    [InlineData("user:profile")]
    [InlineData("   ")]
    public override Task ExistsAsync_WithExistingKey_ReturnsTrue(string cacheKey)
    {
        return base.ExistsAsync_WithExistingKey_ReturnsTrue(cacheKey);
    }

    [Fact]
    public override Task ExistsAsync_WithExpiredKey_ReturnsFalse()
    {
        return base.ExistsAsync_WithExpiredKey_ReturnsFalse();
    }

    [Fact]
    public override Task ExistsAsync_WithNonExistentKey_ReturnsFalse()
    {
        return base.ExistsAsync_WithNonExistentKey_ReturnsFalse();
    }

    [Fact]
    public override Task ExistsAsync_WithNullKey_ThrowsArgumentNullException()
    {
        return base.ExistsAsync_WithNullKey_ThrowsArgumentNullException();
    }

    [Fact]
    public override Task ExistsAsync_WithNullStoredValue_ReturnsTrue()
    {
        return base.ExistsAsync_WithNullStoredValue_ReturnsTrue();
    }

    [Fact]
    public override Task ExistsAsync_WithScopedCache_ChecksOnlyWithinScope()
    {
        return base.ExistsAsync_WithScopedCache_ChecksOnlyWithinScope();
    }

    [Fact]
    public override Task GetAllAsync_WithEmptyKeys_ReturnsEmpty()
    {
        return base.GetAllAsync_WithEmptyKeys_ReturnsEmpty();
    }

    [Theory]
    [InlineData("test2")]
    [InlineData("   ")]
    public override Task GetAllAsync_WithExistingKeys_ReturnsAllValues(string cacheKey)
    {
        return base.GetAllAsync_WithExistingKeys_ReturnsAllValues(cacheKey);
    }

    [Fact]
    public override Task GetAllAsync_WithKeysContainingEmpty_ThrowsArgumentException()
    {
        return base.GetAllAsync_WithKeysContainingEmpty_ThrowsArgumentException();
    }

    [Fact]
    public override Task GetAllAsync_WithKeysContainingNull_ThrowsArgumentNullException()
    {
        return base.GetAllAsync_WithKeysContainingNull_ThrowsArgumentNullException();
    }

    [Fact]
    public override Task GetAllAsync_WithMixedCaseKeys_RetrievesExactMatches()
    {
        return base.GetAllAsync_WithMixedCaseKeys_RetrievesExactMatches();
    }

    [Fact]
    public override Task GetAllAsync_WithMixedObjectTypes_ReturnsCorrectValues()
    {
        return base.GetAllAsync_WithMixedObjectTypes_ReturnsCorrectValues();
    }

    [Fact]
    public override Task GetAllAsync_WithNonExistentKeys_ReturnsEmptyResults()
    {
        return base.GetAllAsync_WithNonExistentKeys_ReturnsEmptyResults();
    }

    [Fact]
    public override Task GetAllAsync_WithNullKeys_ThrowsArgumentNullException()
    {
        return base.GetAllAsync_WithNullKeys_ThrowsArgumentNullException();
    }

    [Fact]
    public override Task GetAllAsync_WithNullValues_HandlesNullsCorrectly()
    {
        return base.GetAllAsync_WithNullValues_HandlesNullsCorrectly();
    }

    [Fact]
    public override Task GetAllAsync_WithOverlappingKeys_UsesLatestValues()
    {
        return base.GetAllAsync_WithOverlappingKeys_UsesLatestValues();
    }

    [Fact]
    public override Task GetAllAsync_WithScopedCache_ReturnsUnscopedKeys()
    {
        return base.GetAllAsync_WithScopedCache_ReturnsUnscopedKeys();
    }

    [Fact]
    public override Task GetAllExpirationAsync_WithExpiredKeys_ExcludesExpiredKeys()
    {
        return base.GetAllExpirationAsync_WithExpiredKeys_ExcludesExpiredKeys();
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(10000)]
    public override Task GetAllExpirationAsync_WithLargeNumberOfKeys_ReturnsAllExpirations(int count)
    {
        return base.GetAllExpirationAsync_WithLargeNumberOfKeys_ReturnsAllExpirations(count);
    }

    [Fact]
    public override Task GetAllExpirationAsync_WithMixedKeys_ReturnsOnlyKeysWithExpiration()
    {
        return base.GetAllExpirationAsync_WithMixedKeys_ReturnsOnlyKeysWithExpiration();
    }

    [Fact]
    public override Task GetAsync_WithComplexObject_PreservesAllProperties()
    {
        return base.GetAsync_WithComplexObject_PreservesAllProperties();
    }

    [Theory]
    [InlineData("order:details")]
    [InlineData("   ")]
    public override Task GetAsync_WithComplexObject_ReturnsNewInstance(string cacheKey)
    {
        return base.GetAsync_WithComplexObject_ReturnsNewInstance(cacheKey);
    }

    [Fact]
    public override Task GetAsync_WithDifferentCasedKeys_TreatsAsDifferentKeys()
    {
        return base.GetAsync_WithDifferentCasedKeys_TreatsAsDifferentKeys();
    }

    [Fact]
    public override Task GetAsync_WithEmptyKey_ThrowsArgumentException()
    {
        return base.GetAsync_WithEmptyKey_ThrowsArgumentException();
    }

    [Fact]
    public override Task GetAsync_WithLargeNumber_ReturnsCorrectValue()
    {
        return base.GetAsync_WithLargeNumber_ReturnsCorrectValue();
    }

    [Fact]
    public override Task GetAsync_WithMaxLongAsInt_ThrowsException()
    {
        return base.GetAsync_WithMaxLongAsInt_ThrowsException();
    }

    [Fact]
    public override Task GetAsync_WithNonExistentKey_ReturnsNoValue()
    {
        return base.GetAsync_WithNonExistentKey_ReturnsNoValue();
    }

    [Fact]
    public override Task GetAsync_WithNullKey_ThrowsArgumentNullException()
    {
        return base.GetAsync_WithNullKey_ThrowsArgumentNullException();
    }

    [Fact]
    public override Task GetAsync_WithNumericTypeConversion_ConvertsIntToLong()
    {
        return base.GetAsync_WithNumericTypeConversion_ConvertsIntToLong();
    }

    [Fact]
    public override Task GetAsync_WithNumericTypeConversion_ConvertsLongToInt()
    {
        return base.GetAsync_WithNumericTypeConversion_ConvertsLongToInt();
    }

    [Fact]
    public override Task GetAsync_WithTryGetSemanticsAndComplexTypeAsLong_ReturnsNoValue()
    {
        return base.GetAsync_WithTryGetSemanticsAndComplexTypeAsLong_ReturnsNoValue();
    }

    [Fact]
    public override Task GetAsync_WithTryGetSemanticsAndIntAsLong_ConvertsSuccessfully()
    {
        return base.GetAsync_WithTryGetSemanticsAndIntAsLong_ConvertsSuccessfully();
    }

    [Fact]
    public override Task GetAsync_WithTryGetSemanticsAndMaxLongAsInt_ReturnsNoValue()
    {
        return base.GetAsync_WithTryGetSemanticsAndMaxLongAsInt_ReturnsNoValue();
    }

    [Fact]
    public override Task GetExpirationAsync_AfterExpiry_ReturnsNull()
    {
        return base.GetExpirationAsync_AfterExpiry_ReturnsNull();
    }

    [Fact]
    public override Task GetExpirationAsync_WithDifferentCasedKeys_GetsExactMatch()
    {
        return base.GetExpirationAsync_WithDifferentCasedKeys_GetsExactMatch();
    }

    [Fact]
    public override Task GetExpirationAsync_WithEmptyKey_ThrowsArgumentException()
    {
        return base.GetExpirationAsync_WithEmptyKey_ThrowsArgumentException();
    }

    [Theory]
    [InlineData("token:refresh")]
    [InlineData("   ")]
    public override Task GetExpirationAsync_WithExpiration_ReturnsCorrectTimeSpan(string cacheKey)
    {
        return base.GetExpirationAsync_WithExpiration_ReturnsCorrectTimeSpan(cacheKey);
    }

    [Fact]
    public override Task GetExpirationAsync_WithExpiredKey_ReturnsNull()
    {
        return base.GetExpirationAsync_WithExpiredKey_ReturnsNull();
    }

    [Fact]
    public override Task GetExpirationAsync_WithNoExpiration_ReturnsNull()
    {
        return base.GetExpirationAsync_WithNoExpiration_ReturnsNull();
    }

    [Fact]
    public override Task GetExpirationAsync_WithNonExistentKey_ReturnsNull()
    {
        return base.GetExpirationAsync_WithNonExistentKey_ReturnsNull();
    }

    [Fact]
    public override Task GetExpirationAsync_WithNullKey_ThrowsArgumentNullException()
    {
        return base.GetExpirationAsync_WithNullKey_ThrowsArgumentNullException();
    }

    [Fact]
    public override Task GetListAsync_WithEmptyKey_ThrowsArgumentException()
    {
        return base.GetListAsync_WithEmptyKey_ThrowsArgumentException();
    }

    [Fact]
    public override Task GetListAsync_WithExpiredItems_RemovesExpiredAndReturnsActive()
    {
        return base.GetListAsync_WithExpiredItems_RemovesExpiredAndReturnsActive();
    }

    [Fact]
    public override Task GetListAsync_WithInvalidPageNumber_ThrowsArgumentOutOfRangeException()
    {
        return base.GetListAsync_WithInvalidPageNumber_ThrowsArgumentOutOfRangeException();
    }

    [Fact]
    public override Task GetListAsync_WithMultiplePages_ReturnsAllItems()
    {
        return base.GetListAsync_WithMultiplePages_ReturnsAllItems();
    }

    [Fact]
    public override Task GetListAsync_WithNewItemsAdded_ReturnsNewItemsLast()
    {
        return base.GetListAsync_WithNewItemsAdded_ReturnsNewItemsLast();
    }

    [Fact]
    public override Task GetListAsync_WithNullKey_ThrowsArgumentNullException()
    {
        return base.GetListAsync_WithNullKey_ThrowsArgumentNullException();
    }

    [Fact]
    public override Task GetListAsync_WithPageBeyondEnd_ReturnsEmptyCollection()
    {
        return base.GetListAsync_WithPageBeyondEnd_ReturnsEmptyCollection();
    }

    [Theory]
    [InlineData("cart:items")]
    [InlineData("   ")]
    public override Task GetListAsync_WithPaging_ReturnsCorrectPageSize(string cacheKey)
    {
        return base.GetListAsync_WithPaging_ReturnsCorrectPageSize(cacheKey);
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
    public override Task IncrementAsync_WithDifferentCasedKeys_IncrementsDistinctCounters()
    {
        return base.IncrementAsync_WithDifferentCasedKeys_IncrementsDistinctCounters();
    }

    [Fact]
    public override Task IncrementAsync_WithEmptyKey_ThrowsArgumentException()
    {
        return base.IncrementAsync_WithEmptyKey_ThrowsArgumentException();
    }

    [Theory]
    [InlineData("metrics:page-views")]
    [InlineData("   ")]
    public override Task IncrementAsync_WithExistingKey_IncrementsValue(string cacheKey)
    {
        return base.IncrementAsync_WithExistingKey_IncrementsValue(cacheKey);
    }

    [Fact]
    public override Task IncrementAsync_WithExpiration_ExpiresCorrectly()
    {
        return base.IncrementAsync_WithExpiration_ExpiresCorrectly();
    }

    [Fact]
    public override Task IncrementAsync_WithNonExistentKey_InitializesToOne()
    {
        return base.IncrementAsync_WithNonExistentKey_InitializesToOne();
    }

    [Fact]
    public override Task IncrementAsync_WithNullKey_ThrowsArgumentNullException()
    {
        return base.IncrementAsync_WithNullKey_ThrowsArgumentNullException();
    }

    [Fact]
    public override Task IncrementAsync_WithScopedCache_WorksWithinScope()
    {
        return base.IncrementAsync_WithScopedCache_WorksWithinScope();
    }

    [Fact]
    public override Task IncrementAsync_WithSpecifiedAmount_IncrementsCorrectly()
    {
        return base.IncrementAsync_WithSpecifiedAmount_IncrementsCorrectly();
    }

    [Fact]
    public override Task ListAddAsync_WithDifferentCasedKeys_MaintainsDistinctLists()
    {
        return base.ListAddAsync_WithDifferentCasedKeys_MaintainsDistinctLists();
    }

    [Theory]
    [InlineData("cart:items")]
    [InlineData("   ")]
    public override Task ListAddAsync_WithDuplicates_RemovesDuplicatesAndAddsItems(string cacheKey)
    {
        return base.ListAddAsync_WithDuplicates_RemovesDuplicatesAndAddsItems(cacheKey);
    }

    [Fact]
    public override Task ListAddAsync_WithDuplicates_StoresUniqueValuesOnly()
    {
        return base.ListAddAsync_WithDuplicates_StoresUniqueValuesOnly();
    }

    [Fact]
    public override Task ListAddAsync_WithEmptyCollection_NoOp()
    {
        return base.ListAddAsync_WithEmptyCollection_NoOp();
    }

    [Fact]
    public override Task ListAddAsync_WithEmptyKey_ThrowsArgumentException()
    {
        return base.ListAddAsync_WithEmptyKey_ThrowsArgumentException();
    }

    [Fact]
    public override Task ListAddAsync_WithExistingNonListKey_ThrowsException()
    {
        return base.ListAddAsync_WithExistingNonListKey_ThrowsException();
    }

    [Fact]
    public override Task ListAddAsync_WithFutureExpiration_AddsAndExpiresCorrectly()
    {
        return base.ListAddAsync_WithFutureExpiration_AddsAndExpiresCorrectly();
    }

    [Fact]
    public override Task ListAddAsync_WithMultipleExpirations_ExpiresIndividualItems()
    {
        return base.ListAddAsync_WithMultipleExpirations_ExpiresIndividualItems();
    }

    [Fact]
    public override Task ListAddAsync_WithNullCollection_ThrowsArgumentNullException()
    {
        return base.ListAddAsync_WithNullCollection_ThrowsArgumentNullException();
    }

    [Fact]
    public override Task ListAddAsync_WithNullItem_IgnoresNull()
    {
        return base.ListAddAsync_WithNullItem_IgnoresNull();
    }

    [Fact]
    public override Task ListAddAsync_WithNullKey_ThrowsArgumentNullException()
    {
        return base.ListAddAsync_WithNullKey_ThrowsArgumentNullException();
    }

    [Fact]
    public override Task ListAddAsync_WithNullValues_ThrowsArgumentNullException()
    {
        return base.ListAddAsync_WithNullValues_ThrowsArgumentNullException();
    }

    [Fact]
    public override Task ListAddAsync_WithPastExpiration_RemovesItem()
    {
        return base.ListAddAsync_WithPastExpiration_RemovesItem();
    }

    [Fact]
    public override Task ListAddAsync_WithSingleString_StoresAsStringNotCharArray()
    {
        return base.ListAddAsync_WithSingleString_StoresAsStringNotCharArray();
    }

    [Fact]
    public override Task ListRemoveAsync_WithEmptyKey_ThrowsArgumentException()
    {
        return base.ListRemoveAsync_WithEmptyKey_ThrowsArgumentException();
    }

    [Theory]
    [InlineData("cart:items")]
    [InlineData("   ")]
    public override Task ListRemoveAsync_WithMultipleValues_RemovesAll(string cacheKey)
    {
        return base.ListRemoveAsync_WithMultipleValues_RemovesAll(cacheKey);
    }

    [Fact]
    public override Task ListRemoveAsync_WithNullCollection_ThrowsArgumentNullException()
    {
        return base.ListRemoveAsync_WithNullCollection_ThrowsArgumentNullException();
    }

    [Fact]
    public override Task ListRemoveAsync_WithNullItem_IgnoresNull()
    {
        return base.ListRemoveAsync_WithNullItem_IgnoresNull();
    }

    [Fact]
    public override Task ListRemoveAsync_WithNullKey_ThrowsArgumentNullException()
    {
        return base.ListRemoveAsync_WithNullKey_ThrowsArgumentNullException();
    }

    [Fact]
    public override Task ListRemoveAsync_WithNullValues_ThrowsArgumentNullException()
    {
        return base.ListRemoveAsync_WithNullValues_ThrowsArgumentNullException();
    }

    [Fact]
    public override Task ListRemoveAsync_WithSingleValue_RemovesCorrectly()
    {
        return base.ListRemoveAsync_WithSingleValue_RemovesCorrectly();
    }

    [Fact]
    public override Task ListRemoveAsync_WithValidValues_RemovesKeyWhenEmpty()
    {
        return base.ListRemoveAsync_WithValidValues_RemovesKeyWhenEmpty();
    }

    [Fact]
    public override Task RemoveAllAsync_WithEmptyKeys_Succeeds()
    {
        return base.RemoveAllAsync_WithEmptyKeys_Succeeds();
    }

    [Fact]
    public override Task RemoveAllAsync_WithKeysContainingEmpty_ThrowsArgumentException()
    {
        return base.RemoveAllAsync_WithKeysContainingEmpty_ThrowsArgumentException();
    }

    [Fact]
    public override Task RemoveAllAsync_WithKeysContainingNull_ThrowsArgumentNullException()
    {
        return base.RemoveAllAsync_WithKeysContainingNull_ThrowsArgumentNullException();
    }

    [Fact]
    public override Task RemoveAllAsync_WithLargeNumberOfKeys_RemovesAllKeysEfficiently()
    {
        return base.RemoveAllAsync_WithLargeNumberOfKeys_RemovesAllKeysEfficiently();
    }

    [Fact]
    public override Task RemoveAllAsync_WithMixedCaseKeys_RemovesOnlyExactMatches()
    {
        return base.RemoveAllAsync_WithMixedCaseKeys_RemovesOnlyExactMatches();
    }

    [Fact]
    public override Task RemoveAllAsync_WithNullKeys_RemovesAllValues()
    {
        return base.RemoveAllAsync_WithNullKeys_RemovesAllValues();
    }

    [Fact]
    public override Task RemoveAllAsync_WithScopedCache_AffectsOnlyScopedKeys()
    {
        return base.RemoveAllAsync_WithScopedCache_AffectsOnlyScopedKeys();
    }

    [Theory]
    [InlineData("remove-all-keys:")]
    [InlineData("   ")]
    public override Task RemoveAllAsync_WithSpecificKeyCollection_RemovesOnlySpecifiedKeys(string keyPrefix)
    {
        return base.RemoveAllAsync_WithSpecificKeyCollection_RemovesOnlySpecifiedKeys(keyPrefix);
    }

    [Fact]
    public override Task RemoveAsync_AfterSetAndGet_RemovesCorrectly()
    {
        return base.RemoveAsync_AfterSetAndGet_RemovesCorrectly();
    }

    [Fact]
    public override Task RemoveAsync_MultipleTimes_Succeeds()
    {
        return base.RemoveAsync_MultipleTimes_Succeeds();
    }

    [Fact]
    public override Task RemoveAsync_WithEmptyKey_ThrowsArgumentException()
    {
        return base.RemoveAsync_WithEmptyKey_ThrowsArgumentException();
    }

    [Theory]
    [InlineData("session:active")]
    [InlineData("   ")]
    public override Task RemoveAsync_WithExistingKey_RemovesSuccessfully(string cacheKey)
    {
        return base.RemoveAsync_WithExistingKey_RemovesSuccessfully(cacheKey);
    }

    [Fact]
    public override Task RemoveAsync_WithExpiredKey_Succeeds()
    {
        return base.RemoveAsync_WithExpiredKey_Succeeds();
    }

    [Fact]
    public override Task RemoveAsync_WithNonExistentKey_Succeeds()
    {
        return base.RemoveAsync_WithNonExistentKey_Succeeds();
    }

    [Fact]
    public override Task RemoveAsync_WithNullKey_ThrowsArgumentNullException()
    {
        return base.RemoveAsync_WithNullKey_ThrowsArgumentNullException();
    }

    [Fact]
    public override Task RemoveAsync_WithNullValue_RemovesSuccessfully()
    {
        return base.RemoveAsync_WithNullValue_RemovesSuccessfully();
    }

    [Fact]
    public override Task RemoveAsync_WithScopedCache_RemovesOnlyWithinScope()
    {
        return base.RemoveAsync_WithScopedCache_RemovesOnlyWithinScope();
    }

    [Fact]
    public override Task RemoveAsync_WithSpecificCase_RemovesOnlyMatchingKey()
    {
        return base.RemoveAsync_WithSpecificCase_RemovesOnlyMatchingKey();
    }

    [Fact]
    public override Task RemoveByPrefixAsync_AsteriskPrefixWithScopedCache_TreatedAsLiteral()
    {
        return base.RemoveByPrefixAsync_AsteriskPrefixWithScopedCache_TreatedAsLiteral();
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
    public override Task RemoveByPrefixAsync_WithCaseSensitivePrefix_RemovesOnlyMatchingCase()
    {
        return base.RemoveByPrefixAsync_WithCaseSensitivePrefix_RemovesOnlyMatchingCase();
    }

    [Fact]
    public override Task RemoveByPrefixAsync_WithDifferentCasedScopes_RemovesOnlyMatchingScope()
    {
        return base.RemoveByPrefixAsync_WithDifferentCasedScopes_RemovesOnlyMatchingScope();
    }

    [Fact]
    public override Task RemoveByPrefixAsync_WithDoubleAsteriskPrefix_TreatsAsLiteral()
    {
        return base.RemoveByPrefixAsync_WithDoubleAsteriskPrefix_TreatsAsLiteral();
    }

    [Fact]
    public override Task RemoveByPrefixAsync_WithEmptyPrefix_RemovesAllKeys()
    {
        return base.RemoveByPrefixAsync_WithEmptyPrefix_RemovesAllKeys();
    }

    [Theory]
    [MemberData(nameof(GetLineEndingPrefixes))]
    public override Task RemoveByPrefixAsync_WithLineEndingPrefix_TreatsAsLiteral(string lineEndingPrefix)
    {
        return base.RemoveByPrefixAsync_WithLineEndingPrefix_TreatsAsLiteral(lineEndingPrefix);
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(9999)]
    public override Task RemoveByPrefixAsync_WithMultipleMatchingKeys_RemovesOnlyPrefixedKeys(int count)
    {
        return base.RemoveByPrefixAsync_WithMultipleMatchingKeys_RemovesOnlyPrefixedKeys(count);
    }

    [Fact]
    public override Task RemoveByPrefixAsync_WithNonMatchingPrefix_RemovesZeroKeys()
    {
        return base.RemoveByPrefixAsync_WithNonMatchingPrefix_RemovesZeroKeys();
    }

    [Fact]
    public override Task RemoveByPrefixAsync_WithNullPrefix_RemovesAllKeys()
    {
        return base.RemoveByPrefixAsync_WithNullPrefix_RemovesAllKeys();
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
    [InlineData("blah:")]
    [InlineData("   ")]
    public override Task RemoveByPrefixAsync_WithMatchingPrefix_RemovesOnlyPrefixedKeys(string prefix)
    {
        return base.RemoveByPrefixAsync_WithMatchingPrefix_RemovesOnlyPrefixedKeys(prefix);
    }

    [Theory]
    [MemberData(nameof(GetWildcardPatterns))]
    public override Task RemoveByPrefixAsync_WithWildcardPattern_TreatsAsLiteral(string pattern)
    {
        return base.RemoveByPrefixAsync_WithWildcardPattern_TreatsAsLiteral(pattern);
    }

    [Fact]
    public override Task RemoveIfEqualAsync_WithEmptyKey_ThrowsArgumentException()
    {
        return base.RemoveIfEqualAsync_WithEmptyKey_ThrowsArgumentException();
    }

    [Theory]
    [InlineData("workflow:state")]
    [InlineData("   ")]
    public override Task RemoveIfEqualAsync_WithMatchingValue_ReturnsTrueAndRemoves(string cacheKey)
    {
        return base.RemoveIfEqualAsync_WithMatchingValue_ReturnsTrueAndRemoves(cacheKey);
    }

    [Fact]
    public override Task RemoveIfEqualAsync_WithMismatchedValue_ReturnsFalseAndDoesNotRemove()
    {
        return base.RemoveIfEqualAsync_WithMismatchedValue_ReturnsFalseAndDoesNotRemove();
    }

    [Fact]
    public override Task RemoveIfEqualAsync_WithNullKey_ThrowsArgumentNullException()
    {
        return base.RemoveIfEqualAsync_WithNullKey_ThrowsArgumentNullException();
    }

    [Fact]
    public override Task ReplaceAsync_WithDifferentCasedKeys_TreatsAsDifferentKeys()
    {
        return base.ReplaceAsync_WithDifferentCasedKeys_TreatsAsDifferentKeys();
    }

    [Fact]
    public override Task ReplaceAsync_WithEmptyKey_ThrowsArgumentException()
    {
        return base.ReplaceAsync_WithEmptyKey_ThrowsArgumentException();
    }

    [Theory]
    [InlineData("settings:theme")]
    [InlineData("   ")]
    public override Task ReplaceAsync_WithExistingKey_ReturnsTrueAndReplacesValue(string cacheKey)
    {
        return base.ReplaceAsync_WithExistingKey_ReturnsTrueAndReplacesValue(cacheKey);
    }

    [Fact]
    public override Task ReplaceAsync_WithExpiration_SetsExpirationCorrectly()
    {
        return base.ReplaceAsync_WithExpiration_SetsExpirationCorrectly();
    }

    [Fact]
    public override Task ReplaceAsync_WithNonExistentKey_ReturnsFalseAndDoesNotCreateKey()
    {
        return base.ReplaceAsync_WithNonExistentKey_ReturnsFalseAndDoesNotCreateKey();
    }

    [Fact]
    public override Task ReplaceAsync_WithNullKey_ThrowsArgumentNullException()
    {
        return base.ReplaceAsync_WithNullKey_ThrowsArgumentNullException();
    }

    [Fact]
    public override Task ReplaceIfEqualAsync_WithDifferentCasedKeys_ReplacesOnlyExactMatch()
    {
        return base.ReplaceIfEqualAsync_WithDifferentCasedKeys_ReplacesOnlyExactMatch();
    }

    [Fact]
    public override Task ReplaceIfEqualAsync_WithEmptyKey_ThrowsArgumentException()
    {
        return base.ReplaceIfEqualAsync_WithEmptyKey_ThrowsArgumentException();
    }

    [Fact]
    public override Task ReplaceIfEqualAsync_WithExpiration_SetsExpirationCorrectly()
    {
        return base.ReplaceIfEqualAsync_WithExpiration_SetsExpirationCorrectly();
    }

    [Theory]
    [InlineData("workflow:state")]
    [InlineData("   ")]
    public override Task ReplaceIfEqualAsync_WithMatchingOldValue_ReturnsTrueAndReplacesValue(string cacheKey)
    {
        return base.ReplaceIfEqualAsync_WithMatchingOldValue_ReturnsTrueAndReplacesValue(cacheKey);
    }

    [Fact]
    public override Task ReplaceIfEqualAsync_WithMismatchedOldValue_ReturnsFalseAndDoesNotReplace()
    {
        return base.ReplaceIfEqualAsync_WithMismatchedOldValue_ReturnsFalseAndDoesNotReplace();
    }

    [Fact]
    public override Task ReplaceIfEqualAsync_WithNullKey_ThrowsArgumentNullException()
    {
        return base.ReplaceIfEqualAsync_WithNullKey_ThrowsArgumentNullException();
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
    public override Task SetAllAsync_WithDateTimeMinValue_DoesNotAddKeys()
    {
        return base.SetAllAsync_WithDateTimeMinValue_DoesNotAddKeys();
    }

    [Fact]
    public override Task SetAllAsync_WithDifferentCasedKeys_CreatesDistinctEntries()
    {
        return base.SetAllAsync_WithDifferentCasedKeys_CreatesDistinctEntries();
    }

    [Fact]
    public override Task SetAllAsync_WithLargeNumberOfKeys_MeasuresThroughput()
    {
        return base.SetAllAsync_WithLargeNumberOfKeys_MeasuresThroughput();
    }

    [Fact]
    public override Task SetAllAsync_WithEmptyItems_ReturnsTrue()
    {
        return base.SetAllAsync_WithEmptyItems_ReturnsTrue();
    }

    [Theory]
    [InlineData("test")]
    [InlineData("   ")]
    public override Task SetAllAsync_WithExpiration_KeysExpireCorrectly(string cacheKey)
    {
        return base.SetAllAsync_WithExpiration_KeysExpireCorrectly(cacheKey);
    }

    [Fact]
    public override Task SetAllAsync_WithItemsContainingEmptyKey_ThrowsArgumentException()
    {
        return base.SetAllAsync_WithItemsContainingEmptyKey_ThrowsArgumentException();
    }

    [Fact]
    public override Task SetAllAsync_WithNullItems_ThrowsArgumentNullException()
    {
        return base.SetAllAsync_WithNullItems_ThrowsArgumentNullException();
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(10000)]
    public override Task SetAllExpiration_WithLargeNumberOfKeys_SetsAllExpirations(int count)
    {
        return base.SetAllExpiration_WithLargeNumberOfKeys_SetsAllExpirations(count);
    }

    [Fact]
    public override Task SetAllExpiration_WithMultipleKeys_SetsExpirationForAll()
    {
        return base.SetAllExpiration_WithMultipleKeys_SetsExpirationForAll();
    }

    [Fact]
    public override Task SetAllExpiration_WithNonExistentKeys_HandlesGracefully()
    {
        return base.SetAllExpiration_WithNonExistentKeys_HandlesGracefully();
    }

    [Fact]
    public override Task SetAllExpiration_WithNullValues_RemovesExpiration()
    {
        return base.SetAllExpiration_WithNullValues_RemovesExpiration();
    }

    [Theory]
    [InlineData("user:profile")]
    [InlineData("   ")]
    public override Task SetAsync_WithComplexObject_StoresCorrectly(string cacheKey)
    {
        return base.SetAsync_WithComplexObject_StoresCorrectly(cacheKey);
    }

    [Fact]
    public override Task SetAsync_WithDifferentCasedKeys_CreatesDistinctEntries()
    {
        return base.SetAsync_WithDifferentCasedKeys_CreatesDistinctEntries();
    }

    [Fact]
    public override Task SetAsync_WithDifferentCasedScopes_MaintainsDistinctNamespaces()
    {
        return base.SetAsync_WithDifferentCasedScopes_MaintainsDistinctNamespaces();
    }

    [Fact]
    public override Task SetAsync_WithDifferentScopes_IsolatesKeys()
    {
        return base.SetAsync_WithDifferentScopes_IsolatesKeys();
    }

    [Fact]
    public override Task SetAsync_WithEmptyKey_ThrowsArgumentException()
    {
        return base.SetAsync_WithEmptyKey_ThrowsArgumentException();
    }

    [Fact]
    public override Task SetAsync_WithExpirationEdgeCases_HandlesCorrectly()
    {
        return base.SetAsync_WithExpirationEdgeCases_HandlesCorrectly();
    }

    [Fact]
    public override Task SetAsync_WithLargeNumber_StoresCorrectly()
    {
        return base.SetAsync_WithLargeNumber_StoresCorrectly();
    }

    [Fact]
    public override Task SetAsync_WithLargeNumbersAndExpiration_PreservesValues()
    {
        return base.SetAsync_WithLargeNumbersAndExpiration_PreservesValues();
    }

    [Fact]
    public override Task SetAsync_WithNestedScopes_PreservesHierarchy()
    {
        return base.SetAsync_WithNestedScopes_PreservesHierarchy();
    }

    [Fact]
    public override Task SetAsync_WithNullKey_ThrowsArgumentNullException()
    {
        return base.SetAsync_WithNullKey_ThrowsArgumentNullException();
    }

    [Fact]
    public override Task SetAsync_WithNullReferenceType_StoresAsNullValue()
    {
        return base.SetAsync_WithNullReferenceType_StoresAsNullValue();
    }

    [Fact]
    public override Task SetAsync_WithNullValueType_StoresAsNullValue()
    {
        return base.SetAsync_WithNullValueType_StoresAsNullValue();
    }

    [Fact]
    public override Task SetAsync_WithShortExpiration_ExpiresCorrectly()
    {
        return base.SetAsync_WithShortExpiration_ExpiresCorrectly();
    }

    [Theory]
    [InlineData("token:refresh")]
    [InlineData("   ")]
    public override Task SetExpirationAsync_ChangingFromNoExpirationToFutureTime_UpdatesCorrectly(string cacheKey)
    {
        return base.SetExpirationAsync_ChangingFromNoExpirationToFutureTime_UpdatesCorrectly(cacheKey);
    }

    [Fact]
    public override Task SetExpirationAsync_ChangingToDateTimeMinValue_RemovesKey()
    {
        return base.SetExpirationAsync_ChangingToDateTimeMinValue_RemovesKey();
    }

    [Fact]
    public override Task SetExpirationAsync_WithCurrentTime_ExpiresImmediately()
    {
        return base.SetExpirationAsync_WithCurrentTime_ExpiresImmediately();
    }

    [Fact]
    public override Task SetExpirationAsync_WithDateTimeMaxValue_NeverExpires()
    {
        return base.SetExpirationAsync_WithDateTimeMaxValue_NeverExpires();
    }

    [Fact]
    public override Task SetExpirationAsync_WithDateTimeMinValue_ExpiresImmediately()
    {
        return base.SetExpirationAsync_WithDateTimeMinValue_ExpiresImmediately();
    }

    [Fact]
    public override Task SetExpirationAsync_WithDifferentCasedKeys_SetsOnlyExactMatch()
    {
        return base.SetExpirationAsync_WithDifferentCasedKeys_SetsOnlyExactMatch();
    }

    [Fact]
    public override Task SetExpirationAsync_WithEmptyKey_ThrowsArgumentException()
    {
        return base.SetExpirationAsync_WithEmptyKey_ThrowsArgumentException();
    }

    [Fact]
    public override Task SetExpirationAsync_WithNullKey_ThrowsArgumentNullException()
    {
        return base.SetExpirationAsync_WithNullKey_ThrowsArgumentNullException();
    }

    [Fact]
    public override Task SetIfHigherAsync_WithDateTime_DoesNotUpdateWhenLower()
    {
        return base.SetIfHigherAsync_WithDateTime_DoesNotUpdateWhenLower();
    }

    [Fact]
    public override Task SetIfHigherAsync_WithDateTime_InitializesWhenKeyNotExists()
    {
        return base.SetIfHigherAsync_WithDateTime_InitializesWhenKeyNotExists();
    }

    [Fact]
    public override Task SetIfHigherAsync_WithDateTime_UpdatesWhenHigher()
    {
        return base.SetIfHigherAsync_WithDateTime_UpdatesWhenHigher();
    }

    [Fact]
    public override Task SetIfHigherAsync_WithLargeNumbers_UpdatesWhenHigher()
    {
        return base.SetIfHigherAsync_WithLargeNumbers_UpdatesWhenHigher();
    }

    [Fact]
    public override Task SetIfLowerAsync_WithDateTime_DoesNotUpdateWhenHigher()
    {
        return base.SetIfLowerAsync_WithDateTime_DoesNotUpdateWhenHigher();
    }

    [Fact]
    public override Task SetIfLowerAsync_WithDateTime_InitializesWhenKeyNotExists()
    {
        return base.SetIfLowerAsync_WithDateTime_InitializesWhenKeyNotExists();
    }

    [Fact]
    public override Task SetIfLowerAsync_WithDateTime_UpdatesWhenLower()
    {
        return base.SetIfLowerAsync_WithDateTime_UpdatesWhenLower();
    }

    [Fact]
    public override Task SetIfLowerAsync_WithLargeNumbers_UpdatesWhenLower()
    {
        return base.SetIfLowerAsync_WithLargeNumbers_UpdatesWhenLower();
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
}
