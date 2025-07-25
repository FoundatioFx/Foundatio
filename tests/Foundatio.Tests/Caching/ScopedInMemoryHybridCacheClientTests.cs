using System.Threading.Tasks;
using Foundatio.Caching;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Caching;

public class ScopedInMemoryHybridCacheClientTests : HybridCacheClientTestBase
{
    public ScopedInMemoryHybridCacheClientTests(ITestOutputHelper output) : base(output) { }

    protected override ICacheClient GetCacheClient(bool shouldThrowOnSerializationError = true)
    {
        return new ScopedCacheClient(new InMemoryHybridCacheClient(_messageBus, Log, shouldThrowOnSerializationError), "scoped");
    }

    protected override HybridCacheClient GetDistributedHybridCacheClient(bool shouldThrowOnSerializationError = true)
    {
        return new InMemoryHybridCacheClient(_distributedCache, _messageBus, Log, shouldThrowOnSerializationError);
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
    public override Task CanInvalidateLocalCacheViaRemoveAllAsync()
    {
        return base.CanInvalidateLocalCacheViaRemoveAllAsync();
    }

    [Fact]
    public override Task CanInvalidateLocalCacheViaRemoveByPrefixAsync()
    {
        return base.CanInvalidateLocalCacheViaRemoveByPrefixAsync();
    }

    [Fact(Skip = "Skip because cache invalidation loops on this with 2 in memory cache client instances")]
    public override Task WillUseLocalCache()
    {
        return base.WillUseLocalCache();
    }

    [Fact(Skip = "Skip because cache invalidation loops on this with 2 in memory cache client instances")]
    public override Task WillExpireRemoteItems()
    {
        return base.WillExpireRemoteItems();
    }

    [Fact(Skip = "Skip because cache invalidation loops on this with 2 in memory cache client instances")]
    public override Task WillWorkWithSets()
    {
        return base.WillWorkWithSets();
    }
}
