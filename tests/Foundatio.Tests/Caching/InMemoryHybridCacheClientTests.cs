using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Messaging;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Foundatio.Tests.Caching;

public class InMemoryHybridCacheClientTests : HybridCacheClientTestBase
{
    public InMemoryHybridCacheClientTests(ITestOutputHelper output) : base(output) { }

    protected override ICacheClient GetCacheClient(bool shouldThrowOnSerializationError = true)
    {
        return new InMemoryHybridCacheClient(_messageBus, Log, shouldThrowOnSerializationError);
    }

    protected override HybridCacheClient GetDistributedHybridCacheClient(bool shouldThrowOnSerializationError = true)
    {
        return new InMemoryHybridCacheClient(_distributedCache, _messageBus, Log, shouldThrowOnSerializationError);
    }

    [Theory]
    [InlineData("Add", false)]
    [InlineData("Add", true)]
    [InlineData("Set", false)]
    [InlineData("Set", true)]
    [InlineData("SetAll", false)]
    [InlineData("SetAll", true)]
    [InlineData("Replace", false)]
    [InlineData("Replace", true)]
    [InlineData("ReplaceIfEqual", false)]
    [InlineData("ReplaceIfEqual", true)]
    [InlineData("IncrementLong", false)]
    [InlineData("IncrementLong", true)]
    [InlineData("IncrementDouble", false)]
    [InlineData("IncrementDouble", true)]
    [InlineData("HigherLong", false)]
    [InlineData("HigherLong", true)]
    [InlineData("HigherDouble", false)]
    [InlineData("HigherDouble", true)]
    [InlineData("LowerLong", false)]
    [InlineData("LowerLong", true)]
    [InlineData("LowerDouble", false)]
    [InlineData("LowerDouble", true)]
    public async Task WriteAsync_WhenLocalFallbackExceedsSizeLimit_NextReadReturnsUpdatedValue(string operation, bool shouldThrow)
    {
        bool rejectLocalWrites = false;
        using var distributedCache = new InMemoryCacheClient(o => o.LoggerFactory(Log));
        using var messageBus = new InMemoryMessageBus(o => o.LoggerFactory(Log));
        using var cache = new HybridCacheClient(distributedCache, messageBus, new InMemoryCacheClientOptions
        {
            LoggerFactory = Log,
            MaxMemorySize = 1000,
            MaxEntrySize = 10,
            ShouldThrowOnMaxEntrySizeExceeded = shouldThrow,
            SizeCalculator = value => !rejectLocalWrites || value is 42L ? 3 : throw new InvalidOperationException("Sizing failed")
        }, Log);
        using var peer = new HybridCacheClient(distributedCache, messageBus, loggerFactory: Log);
        const string key = "rejected-local-write";
        Assert.True(await cache.SetAsync(key, 10L));
        Assert.Equal(10L, (await cache.LocalCache.GetAsync<long>(key)).Value);
        Assert.Equal(10L, (await peer.GetAsync<long>(key)).Value);
        if (operation == "Add")
            Assert.True(await distributedCache.RemoveAsync(key));
        rejectLocalWrites = true;

        Task write = operation switch
        {
            "Add" => cache.AddAsync(key, 15L),
            "Set" => cache.SetAsync(key, 15L),
            "SetAll" => cache.SetAllAsync(new Dictionary<string, long> { [key] = 15L, ["accepted-local-write"] = 42L }),
            "Replace" => cache.ReplaceAsync(key, 15L),
            "ReplaceIfEqual" => cache.ReplaceIfEqualAsync(key, 15L, 10L),
            "IncrementLong" => cache.IncrementAsync(key, 5L),
            "IncrementDouble" => cache.IncrementAsync(key, 5d),
            "HigherLong" => cache.SetIfHigherAsync(key, 15L),
            "HigherDouble" => cache.SetIfHigherAsync(key, 15d),
            "LowerLong" => cache.SetIfLowerAsync(key, 5L),
            "LowerDouble" => cache.SetIfLowerAsync(key, 5d),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        if (shouldThrow)
        {
            await Assert.ThrowsAsync<MaxEntrySizeExceededCacheException>(() => write);
            Assert.False((await cache.LocalCache.GetAsync<long>(key)).HasValue);
            rejectLocalWrites = false;
        }
        else
        {
            await write;
        }

        long expected = operation.StartsWith("Lower", StringComparison.Ordinal) ? 5L : 15L;
        Assert.Equal(expected, (await distributedCache.GetAsync<long>(key)).Value);
        Assert.Equal(expected, (await cache.GetAsync<long>(key)).Value);
        if (operation != "Add")
            Assert.Equal(expected, (await peer.GetAsync<long>(key)).Value);
        if (operation == "SetAll")
            Assert.Equal(42L, (await cache.GetAsync<long>("accepted-local-write")).Value);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task ListWriteAsync_WhenLocalFallbackExceedsSizeLimit_NextReadReturnsUpdatedValues(bool remove, bool stringValue, bool shouldThrow)
    {
        bool rejectLocalWrites = false;
        using var distributedCache = new InMemoryCacheClient(o => o.LoggerFactory(Log));
        using var messageBus = new InMemoryMessageBus(o => o.LoggerFactory(Log));
        using var cache = new HybridCacheClient(distributedCache, messageBus, new InMemoryCacheClientOptions
        {
            LoggerFactory = Log,
            MaxMemorySize = 1000,
            MaxEntrySize = 10,
            ShouldThrowOnMaxEntrySizeExceeded = shouldThrow,
            SizeCalculator = _ => !rejectLocalWrites ? 3 : throw new InvalidOperationException("Sizing failed")
        }, Log);
        using var peer = new HybridCacheClient(distributedCache, messageBus, loggerFactory: Log);
        const string key = "rejected-local-list-write";
        Assert.Equal(2, await cache.ListAddAsync(key, ["old", "keep"]));
        Assert.Equal(["keep", "old"], (await cache.LocalCache.GetListAsync<string>(key)).Value!.Order());
        Assert.Equal(["keep", "old"], (await peer.GetListAsync<string>(key)).Value!.Order());
        rejectLocalWrites = true;

        var write = (remove, stringValue) switch
        {
            (false, false) => cache.ListAddAsync(key, ["new"]),
            (false, true) => cache.ListAddAsync(key, "new"),
            (true, false) => cache.ListRemoveAsync(key, ["old"]),
            (true, true) => cache.ListRemoveAsync(key, "old")
        };
        if (shouldThrow)
        {
            await Assert.ThrowsAsync<MaxEntrySizeExceededCacheException>(() => write);
            Assert.False((await cache.LocalCache.GetListAsync<string>(key)).HasValue);
            rejectLocalWrites = false;
        }
        else
        {
            Assert.Equal(1, await write);
        }

        string[] expected = remove ? ["keep"] : ["keep", "new", "old"];
        Assert.Equal(expected, (await distributedCache.GetListAsync<string>(key)).Value!.Order());
        Assert.Equal(expected, (await cache.GetListAsync<string>(key)).Value!.Order());
        Assert.Equal(expected, (await peer.GetListAsync<string>(key)).Value!.Order());
    }

    [Fact]
    public async Task SetAsync_WhenReplacementExpiresDuringLocalSizing_DoesNotReturnOldValue()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var distributedCache = new InMemoryCacheClient(o => o.TimeProvider(clock).LoggerFactory(Log));
        using var messageBus = new InMemoryMessageBus(o => o.TimeProvider(clock).LoggerFactory(Log));
        using var cache = new HybridCacheClient(distributedCache, messageBus, new InMemoryCacheClientOptions
        {
            TimeProvider = clock,
            LoggerFactory = Log,
            MaxMemorySize = 1000,
            SizeCalculator = value =>
            {
                if (value is "replacement")
                    clock.Advance(TimeSpan.FromSeconds(1));
                return 3;
            }
        }, Log);
        const string key = "expired-local-write";
        Assert.True(await cache.SetAsync(key, "old", TimeSpan.FromMinutes(1)));

        Assert.True(await cache.SetAsync(key, "replacement", TimeSpan.FromMilliseconds(10)));

        Assert.False((await distributedCache.GetAsync<string>(key)).HasValue);
        Assert.False((await cache.GetAsync<string>(key)).HasValue);
    }

    [Fact]
    public async Task SetAsync_WhenLocalWriteAndInvalidationThrow_PreservesWriteError()
    {
        bool failInvalidation = false;
        var messageBus = new Mock<IMessageBus>();
        messageBus.Setup(b => b.SubscribeAsync(It.IsAny<Func<HybridCacheClient.InvalidateCache, CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        messageBus.Setup(b => b.PublishAsync(typeof(HybridCacheClient.InvalidateCache), It.IsAny<object>(), It.IsAny<MessageOptions?>(), It.IsAny<CancellationToken>()))
            .Returns(() => failInvalidation ? Task.FromException(new InvalidOperationException("Invalidation failed")) : Task.CompletedTask);
        using var distributedCache = new InMemoryCacheClient(o => o.LoggerFactory(Log));
        using var cache = new HybridCacheClient(distributedCache, messageBus.Object, new InMemoryCacheClientOptions
        {
            LoggerFactory = Log,
            MaxMemorySize = 1000,
            MaxEntrySize = 10,
            ShouldThrowOnMaxEntrySizeExceeded = true,
            SizeCalculator = value => value is "old" ? 3 : throw new InvalidOperationException("Sizing failed")
        }, Log);
        const string key = "failed-local-write";
        Assert.True(await cache.SetAsync(key, "old"));
        failInvalidation = true;

        await Assert.ThrowsAsync<MaxEntrySizeExceededCacheException>(() => cache.SetAsync(key, "replacement"));

        Assert.Equal("replacement", (await distributedCache.GetAsync<string>(key)).Value);
        Assert.False((await cache.LocalCache.GetAsync<string>(key)).HasValue);
        messageBus.Verify(b => b.PublishAsync(typeof(HybridCacheClient.InvalidateCache), It.IsAny<object>(), It.IsAny<MessageOptions?>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public override Task AddAsync_WithConcurrentAcquireAndRelease_OnlyOneOwnerAtATime()
    {
        return base.AddAsync_WithConcurrentAcquireAndRelease_OnlyOneOwnerAtATime();
    }

    [Fact]
    public override Task AddAsync_WithConcurrentRequests_OnlyOneSucceeds()
    {
        return base.AddAsync_WithConcurrentRequests_OnlyOneSucceeds();
    }

    [Fact(Skip = "Skip because cache invalidation loops on this with 2 in memory cache client instances")]
    public override Task AddAsync_WithExpiration_ExpiresRemoteItems()
    {
        return base.AddAsync_WithExpiration_ExpiresRemoteItems();
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

    [Fact(Skip = "Skip because HybridCacheClient can cache a stale local value under concurrent writes: https://github.com/FoundatioFx/Foundatio/issues/572")]
    public override Task IncrementAsync_WithConcurrentRequests_DoesNotLoseUpdates()
    {
        return base.IncrementAsync_WithConcurrentRequests_DoesNotLoseUpdates();
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
    public override Task ListAddAsync_WithConcurrentRequests_DoesNotLoseValues()
    {
        return base.ListAddAsync_WithConcurrentRequests_DoesNotLoseValues();
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
    public override Task RemoveByPrefixAsync_FromScopedCache_RemovesOnlyScopedKeys(string? prefixToRemove, int expectedRemovedCount)
    {
        return base.RemoveByPrefixAsync_FromScopedCache_RemovesOnlyScopedKeys(prefixToRemove, expectedRemovedCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public override Task RemoveByPrefixAsync_NullOrEmptyPrefixWithScopedCache_RemovesCorrectKeys(string? prefix)
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
    public override Task RemoveIfEqualAsync_WithNonExistentKey_ReturnsFalse()
    {
        return base.RemoveIfEqualAsync_WithNonExistentKey_ReturnsFalse();
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
    public override Task ReplaceIfEqualAsync_WithNonExistentKey_ReturnsFalseAndDoesNotCreateKey()
    {
        return base.ReplaceIfEqualAsync_WithNonExistentKey_ReturnsFalseAndDoesNotCreateKey();
    }

    [Fact]
    public override Task ReplaceIfEqualAsync_WithMultipleInstances_InvalidatesOtherClientLocalCache()
    {
        return base.ReplaceIfEqualAsync_WithMultipleInstances_InvalidatesOtherClientLocalCache();
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

    [Fact(Skip = "Skip because HybridCacheClient can cache a stale local value under concurrent writes: https://github.com/FoundatioFx/Foundatio/issues/572")]
    public override Task SetIfHigherAsync_WithConcurrentRequests_DifferencesSumToMaximum()
    {
        return base.SetIfHigherAsync_WithConcurrentRequests_DifferencesSumToMaximum();
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
    public override Task SetIfHigherAsync_WithLowerValue_ReturnsZeroAndKeepsValue()
    {
        return base.SetIfHigherAsync_WithLowerValue_ReturnsZeroAndKeepsValue();
    }

    [Fact(Skip = "Skip because HybridCacheClient can cache a stale local value under concurrent writes: https://github.com/FoundatioFx/Foundatio/issues/572")]
    public override Task SetIfLowerAsync_WithConcurrentRequests_DifferencesSumToDecrease()
    {
        return base.SetIfLowerAsync_WithConcurrentRequests_DifferencesSumToDecrease();
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
}

public class InMemoryHybridCacheClient : HybridCacheClient
{
    public InMemoryHybridCacheClient(ICacheClient cacheClient, IMessageBus messageBus, ILoggerFactory loggerFactory, bool shouldThrowOnSerializationError)
        : base(cacheClient, messageBus, new InMemoryCacheClientOptions
        {
            CloneValues = true,
            ShouldThrowOnSerializationError = shouldThrowOnSerializationError,
            TimeProvider = messageBus.GetTimeProvider(),
            LoggerFactory = loggerFactory
        }, loggerFactory)
    {
    }

    public InMemoryHybridCacheClient(IMessageBus messageBus, ILoggerFactory loggerFactory, bool shouldThrowOnSerializationError)
        : base(new InMemoryCacheClient(o => o.LoggerFactory(loggerFactory).ShouldThrowOnSerializationError(shouldThrowOnSerializationError)), messageBus, new InMemoryCacheClientOptions
        {
            CloneValues = true,
            ShouldThrowOnSerializationError = shouldThrowOnSerializationError,
            TimeProvider = messageBus.GetTimeProvider(),
            LoggerFactory = loggerFactory
        }, loggerFactory)
    {
    }

    public override void Dispose()
    {
        base.Dispose();
        _distributedCache.Dispose();
        _messageBus.Dispose();
    }
}
