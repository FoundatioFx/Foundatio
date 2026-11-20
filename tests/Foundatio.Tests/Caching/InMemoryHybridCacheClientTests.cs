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
    public async Task GetAllAsync_WhenPeerWritesDuringRead_CachesOnlyCurrentValues()
    {
        // Arrange
        using var distributed = new DelayedCache(Log);
        using var cache = CreateCache(distributedCache: distributed);
        using var peer = CreateCache(distributedCache: distributed);
        await distributed.SetAllAsync(new Dictionary<string, int> { ["changed"] = 1, ["stable"] = 3 });
        distributed.AfterNextRead(async () => Assert.True(await peer.SetAsync("changed", 2)));

        // Act
        var snapshot = await cache.GetAllAsync<int>(["changed", "stable"]).WaitAsync(TimeSpan.FromSeconds(5), TestCancellationToken);
        var current = await cache.GetAllAsync<int>(["changed", "stable"]);

        // Assert
        Assert.Equal(1, snapshot["changed"].Value);
        Assert.Equal(2, current["changed"].Value);
        Assert.Equal(3, current["stable"].Value);
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

    [Theory]
    [InlineData("scalar")]
    [InlineData("bulk")]
    [InlineData("list")]
    public async Task GetAsync_WhenKeyExpiresBeforeTtlLookup_DoesNotCacheExpiredResult(string operation)
    {
        // Arrange
        const string key = "expires-during-read";
        var clock = new FakeTimeProvider();
        var ttl = TimeSpan.FromSeconds(1);
        using var distributed = new DelayedCache(Log, clock);
        using var cache = CreateCache(new InMemoryCacheClientOptions { TimeProvider = clock }, distributed);
        if (String.Equals(operation, "list", StringComparison.Ordinal))
            await distributed.ListAddAsync(key, [1], ttl);
        else
            await distributed.SetAsync(key, 1, ttl);

        distributed.AfterNextRead(() =>
        {
            clock.Advance(ttl + TimeSpan.FromSeconds(1));
            return Task.CompletedTask;
        });

        // Act
        bool found = operation switch
        {
            "scalar" => (await cache.GetAsync<int>(key)).HasValue,
            "bulk" => (await cache.GetAllAsync<int>([key]))[key].HasValue,
            "list" => (await cache.GetListAsync<int>(key)).HasValue,
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

        // Assert
        Assert.True(found);
        Assert.False(await distributed.ExistsAsync(key));
        Assert.False(await cache.ExistsAsync(key));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task GetAsync_WhenWriteOverlapsReadThrough_DoesNotCacheStaleValue(bool usePeer, bool refreshFails)
    {
        // Arrange
        const string key = "read-through-race";
        using var distributed = new DelayedCache(Log);
        using var cache = CreateCache(distributedCache: distributed);
        using var peer = CreateCache(distributedCache: distributed);
        var writer = usePeer ? peer : cache;
        await distributed.SetAsync(key, 1);
        distributed.AfterNextRead(async () =>
        {
            Assert.True(await writer.SetAsync(key, 2));
            if (refreshFails)
                distributed.AfterNextRead(() => Task.FromException(new CacheException("Refresh failed")));
        });

        // Act
        var staleRead = await cache.GetAsync<int>(key).WaitAsync(TimeSpan.FromSeconds(5), TestCancellationToken);
        var nextRead = await cache.GetAsync<int>(key);

        // Assert
        Assert.Equal(1, staleRead.Value);
        Assert.Equal(2, nextRead.Value);
        Assert.Equal(2, (await distributed.GetAsync<int>(key)).Value);
        if (usePeer)
            Assert.Equal(refreshFails ? 0 : 1, cache.LocalCacheHits);
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
    public async Task GetListAsync_WhenFirstReadIsPaged_NextReadReturnsCompleteList()
    {
        // Arrange
        using var cache = CreateCache();
        const string key = "paged-cold-list";
        await _distributedCache.ListAddAsync(key, [1, 2, 3]);

        // Act
        var page = await cache.GetListAsync<int>(key, page: 1, pageSize: 1);
        var fullList = await cache.GetListAsync<int>(key);

        // Assert
        Assert.Single(page.Value);
        Assert.Equal([1, 2, 3], fullList.Value.Order());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetListAsync_WhenWriteOverlapsReadThrough_NextReadIncludesNewMember(bool usePeer)
    {
        // Arrange
        const string key = "list-read-through-race";
        using var distributed = new DelayedCache(Log);
        using var cache = CreateCache(distributedCache: distributed);
        using var peer = CreateCache(distributedCache: distributed);
        var writer = usePeer ? peer : cache;
        await distributed.ListAddAsync(key, [1]);
        distributed.AfterNextRead(async () => Assert.Equal(1, await writer.ListAddAsync(key, [2])));

        // Act
        var staleRead = await cache.GetListAsync<int>(key).WaitAsync(TimeSpan.FromSeconds(5), TestCancellationToken);
        var nextRead = await cache.GetListAsync<int>(key);

        // Assert
        Assert.Equal([1], staleRead.Value);
        Assert.Equal([1, 2], nextRead.Value.Order());
        if (usePeer)
            Assert.Equal(1, cache.LocalCacheHits);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListAddAsync_WhenColdWritesCompleteOutOfOrder_PreservesBothMembers(bool useSecondClient)
    {
        // Arrange
        const string key = "overlapping-cold-adds";
        using var distributed = new DelayedCache(Log);
        using var cache = CreateCache(distributedCache: distributed);
        using var peer = CreateCache(distributedCache: distributed);
        var secondWriter = useSecondClient ? peer : cache;

        distributed.AfterNextWrite(async () => Assert.Equal(1, await secondWriter.ListAddAsync(key, [2])));

        // Act
        long added = await cache.ListAddAsync(key, [1]).WaitAsync(TimeSpan.FromSeconds(5), TestCancellationToken);

        // Assert
        Assert.Equal(1, added);
        Assert.Equal([1, 2], (await cache.GetListAsync<int>(key)).Value.Order());
    }

    [Fact]
    public async Task ListAddAsync_WhenLocalEntryIsEvictedDuringWrite_NextReadReturnsAllMembers()
    {
        // Arrange
        const string key = "evicted-list";
        using var distributed = new DelayedCache(Log);
        using var cache = CreateCache(distributedCache: distributed);
        Assert.Equal(1, await cache.ListAddAsync(key, [1]));
        distributed.AfterNextWrite(async () => Assert.True(await cache.LocalCache.RemoveAsync(key)));

        // Act
        long added = await cache.ListAddAsync(key, [2]);

        // Assert
        Assert.Equal(1, added);
        Assert.Equal([1, 2], (await cache.GetListAsync<int>(key)).Value.Order());
    }

    [Fact]
    public async Task ListAddAsync_WhenLocalScalarWasReplacedByDistributedList_ReloadsAllMembers()
    {
        // Arrange
        const string key = "changed-list-type";
        using var cache = CreateCache();
        Assert.True(await cache.SetAsync(key, "old"));
        Assert.True(await _distributedCache.RemoveAsync(key));
        Assert.Equal(1, await _distributedCache.ListAddAsync(key, [1]));

        // Act
        long added = await cache.ListAddAsync(key, [2]);

        // Assert
        Assert.Equal(1, added);
        Assert.Equal([1, 2], (await cache.GetListAsync<int>(key)).Value.Order());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ListAddAsync_WhenOnlyDistributedListExists_PreservesEarlierMembers(bool singleString, bool rejectExistenceChecks)
    {
        // Arrange
        const string key = "cold-list";
        using var distributed = new DelayedCache(Log) { RejectExistenceChecks = rejectExistenceChecks };
        using var cache = CreateCache(distributedCache: distributed);
        await distributed.ListAddAsync(key, ["earlier"]);

        // Act
        long added = singleString ? await cache.ListAddAsync(key, "later") : await cache.ListAddAsync(key, ["later"]);

        // Assert
        Assert.Equal(1, added);
        Assert.Equal(["earlier", "later"], (await cache.GetListAsync<string>(key)).Value.Order());
        Assert.Equal(!rejectExistenceChecks, await cache.LocalCache.ExistsAsync(key));
    }

    [Theory]
    [InlineData("key", false)]
    [InlineData("prefix", false)]
    [InlineData("null-prefix", false)]
    [InlineData("all", false)]
    [InlineData("empty-keys", false)]
    [InlineData("member", false)]
    [InlineData("key", true)]
    [InlineData("prefix", true)]
    [InlineData("all", true)]
    [InlineData("empty-keys", true)]
    [InlineData("member", true)]
    public async Task ListAddAsync_WhenRemovedBeforeResponse_KeepsLocalEntryEvicted(string removal, bool useSecondClient)
    {
        // Arrange
        const string key = "pending:list";
        using var distributed = new DelayedCache(Log);
        using var cache = CreateCache(distributedCache: distributed);
        using var peer = CreateCache(distributedCache: distributed);

        var remover = useSecondClient ? peer : cache;
        distributed.AfterNextWrite(() => removal switch
        {
            "key" => remover.RemoveAsync(key),
            "member" => remover.ListRemoveAsync(key, [1]),
            "prefix" => remover.RemoveByPrefixAsync("pending:"),
            "null-prefix" => remover.RemoveByPrefixAsync(null!),
            "all" => remover.RemoveAllAsync(),
            "empty-keys" => remover.RemoveAllAsync(Array.Empty<string>()),
            _ => throw new ArgumentOutOfRangeException(nameof(removal))
        });

        // Act
        await cache.ListAddAsync(key, [1]).WaitAsync(TimeSpan.FromSeconds(5), TestCancellationToken);

        // Assert
        Assert.False((await cache.GetListAsync<int>(key)).HasValue);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListAddAsync_WhenReplacedBeforeResponse_PreservesReplacement(bool setMultiple)
    {
        // Arrange
        const string key = "pending:replacement";
        using var distributed = new DelayedCache(Log);
        using var cache = CreateCache(distributedCache: distributed);
        distributed.AfterNextWrite(() => setMultiple
            ? cache.SetAllAsync(new Dictionary<string, string> { [key] = "replacement" })
            : cache.SetAsync(key, "replacement"));

        // Act
        await cache.ListAddAsync(key, [1]).WaitAsync(TimeSpan.FromSeconds(5), TestCancellationToken);

        // Assert
        Assert.Equal("replacement", (await cache.GetAsync<string>(key)).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListAddAsync_WhenTtlChangesBeforeResponse_PreservesNewExpiration(bool setMultiple)
    {
        // Arrange
        const string key = "pending:expiration";
        var clock = new FakeTimeProvider();
        using var distributed = new DelayedCache(Log, clock);
        using var cache = CreateCache(new InMemoryCacheClientOptions { TimeProvider = clock }, distributed);
        var ttl = TimeSpan.FromSeconds(5);
        distributed.AfterNextWrite(() => setMultiple
            ? cache.SetAllExpirationAsync(new Dictionary<string, TimeSpan?> { [key] = ttl })
            : cache.SetExpirationAsync(key, ttl));

        // Act
        await cache.ListAddAsync(key, [1]).WaitAsync(TimeSpan.FromSeconds(5), TestCancellationToken);
        clock.Advance(ttl + TimeSpan.FromSeconds(1));

        // Assert
        Assert.False(await distributed.ExistsAsync(key));
        Assert.False((await cache.GetListAsync<int>(key)).HasValue);
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

    public static MatrixTheoryData<bool, bool, bool> ListWriteCases =>
        new([false, true], [false, true], [false, true]);

    [Theory]
    [MemberData(nameof(ListWriteCases))]
    public async Task ListWriteAsync_WhenLocalFallbackExceedsSizeLimit_NextReadReturnsUpdatedValues(bool remove, bool stringValue, bool shouldThrow)
    {
        // Arrange
        bool rejectLocalWrites = false;
        using var cache = CreateCache(SizeLimitedOptions(_ => !rejectLocalWrites ? 3 : throw new InvalidOperationException("Sizing failed"), shouldThrow));
        using var peer = CreateCache();
        const string key = "rejected-local-list-write";
        Assert.Equal(2, await cache.ListAddAsync(key, ["old", "keep"]));
        Assert.Equal(["keep", "old"], (await cache.LocalCache.GetListAsync<string>(key)).Value.Order());
        Assert.Equal(["keep", "old"], (await peer.GetListAsync<string>(key)).Value.Order());
        rejectLocalWrites = true;

        // Act
        var write = (remove, stringValue) switch
        {
            (false, false) => cache.ListAddAsync(key, ["new"]),
            (false, true) => cache.ListAddAsync(key, "new"),
            (true, false) => cache.ListRemoveAsync(key, ["old"]),
            (true, true) => cache.ListRemoveAsync(key, "old")
        };

        // Assert
        if (shouldThrow)
        {
            await Assert.ThrowsAsync<MaxEntrySizeExceededCacheException>(() => write);
            Assert.False((await cache.LocalCache.GetListAsync<string>(key)).HasValue);
        }
        else
        {
            Assert.Equal(1, await write);
        }

        string[] expected = remove ? ["keep"] : ["keep", "new", "old"];
        Assert.Equal(expected, (await _distributedCache.GetListAsync<string>(key)).Value.Order());
        Assert.Equal(expected, (await cache.GetListAsync<string>(key)).Value.Order());
        Assert.Equal(expected, (await peer.GetListAsync<string>(key)).Value.Order());
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
    public async Task SetAllAsync_WhenDistributedBatchPartiallyCommitsBeforeThrowing_InvalidatesBothClients()
    {
        // Arrange
        const string key = "accepted";
        using var distributed = new InMemoryCacheClient(o => o.LoggerFactory(Log).MaxMemorySize(1000)
            .MaxEntrySize(10).ShouldThrowOnMaxEntrySizeExceeded(true).SizeCalculator(value => ((string)value).Length));
        using var cache = CreateCache(distributedCache: distributed);
        using var peer = CreateCache(distributedCache: distributed);
        Assert.True(await cache.SetAsync(key, "old"));
        Assert.Equal("old", (await peer.GetAsync<string>(key)).Value);
        var values = new Dictionary<string, string> { [key] = "new", ["rejected"] = "exceeds the size limit" };

        // Act
        await Assert.ThrowsAsync<MaxEntrySizeExceededCacheException>(() => cache.SetAllAsync(values));

        // Assert
        Assert.Equal("new", (await distributed.GetAsync<string>(key)).Value);
        Assert.Equal("new", (await cache.GetAsync<string>(key)).Value);
        Assert.Equal("new", (await peer.GetAsync<string>(key)).Value);
    }

    [Fact]
    public async Task SetAllAsync_WhenPeerWritesDuringLocalUpdate_ReadsLatestValue()
    {
        // Arrange
        var sizing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var resume = new ManualResetEventSlim();
        using var cache = CreateCache(SizeLimitedOptions(value =>
        {
            if (value is 1)
            {
                sizing.TrySetResult();
                if (!resume.Wait(TimeSpan.FromSeconds(10), TestCancellationToken))
                    throw new TimeoutException("Timed out waiting for the peer write");
            }

            return 1;
        }));

        using var peer = CreateCache();
        var values = new Dictionary<string, int> { ["changed"] = 1, ["stable"] = 3 };

        // Act
        var batch = Task.Run(() => cache.SetAllAsync(values), TestCancellationToken);
        int count;
        try
        {
            await sizing.Task.WaitAsync(TimeSpan.FromSeconds(5), TestCancellationToken);
            Assert.True(await peer.SetAsync("changed", 2).WaitAsync(TimeSpan.FromSeconds(5), TestCancellationToken));
        }
        finally
        {
            resume.Set();
            count = await batch.WaitAsync(TimeSpan.FromSeconds(5), TestCancellationToken);
        }

        // Assert
        Assert.Equal(2, count);
        var current = await cache.GetAllAsync<int>(values.Keys);
        Assert.Equal(2, current["changed"].Value);
        Assert.Equal(3, current["stable"].Value);
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
    public async Task SetAllExpirationAsync_WhenOnlyFirstExpirationCommits_InvalidatesBothClients()
    {
        // Arrange
        var values = new Dictionary<string, int> { ["first"] = 1, ["second"] = 2 };
        var expirations = new Dictionary<string, TimeSpan?> { ["first"] = TimeSpan.Zero, ["second"] = TimeSpan.Zero };
        var error = new CacheException("Batch interrupted");
        var provider = new Mock<ICacheClient>();
        provider.Setup(c => c.SetAllExpirationAsync(expirations)).Returns(async () =>
        {
            await _distributedCache.SetExpirationAsync("first", TimeSpan.Zero);
            throw error;
        });

        using var writer = CreateCache(distributedCache: provider.Object);
        using var peer = CreateCache();
        await peer.SetAllAsync(values);
        await writer.LocalCache.SetAllAsync(values);

        // Act
        var thrown = await Assert.ThrowsAsync<CacheException>(() => writer.SetAllExpirationAsync(expirations));

        // Assert
        Assert.Same(error, thrown);
        Assert.Equal(0, writer.LocalCache.Count);
        Assert.Equal(0, peer.LocalCache.Count);
        var current = await peer.GetAllAsync<int>(values.Keys);
        Assert.False(current["first"].HasValue);
        Assert.Equal(2, current["second"].Value);
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
    public async Task SetAsync_WhenEvictionThrowsAfterLocalWriteFailure_StillInvalidatesPeer()
    {
        // Arrange
        const string key = "failed-eviction";
        var clock = new Mock<TimeProvider> { CallBase = true };
        var options = SizeLimitedOptions(value =>
        {
            if (String.Equals(value as string, "old", StringComparison.Ordinal))
                return 3;

            clock.Setup(c => c.GetUtcNow()).Throws(new InvalidOperationException("Eviction clock failed"));
            return 11;
        });

        options.TimeProvider = clock.Object;
        using var cache = CreateCache(options);
        using var peer = CreateCache();
        Assert.True(await cache.SetAsync(key, "old", TimeSpan.FromMinutes(1)));
        Assert.Equal("old", (await peer.GetAsync<string>(key)).Value);

        // Act
        await Assert.ThrowsAsync<MaxEntrySizeExceededCacheException>(() => cache.SetAsync(key, "exceeds the size limit"));

        // Assert
        Assert.Equal("exceeds the size limit", (await peer.GetAsync<string>(key)).Value);
    }

    [Fact]
    public async Task SetAsync_WhenLocalWriteAndInvalidationThrow_PreservesWriteError()
    {
        // Arrange
        using var cache = CreateCache(SizeLimitedOptions(value =>
            String.Equals(value as string, "old", StringComparison.Ordinal) ? 3 : throw new InvalidOperationException("Sizing failed")));
        const string key = "failed-local-write";
        Assert.True(await cache.SetAsync(key, "old"));
        _messageBus.Dispose();

        // Act
        await Assert.ThrowsAsync<MaxEntrySizeExceededCacheException>(() => cache.SetAsync(key, "replacement"));

        // Assert
        Assert.Equal("replacement", (await _distributedCache.GetAsync<string>(key)).Value);
        Assert.False((await cache.LocalCache.GetAsync<string>(key)).HasValue);
        Assert.Contains(Log.LogEntries, entry => entry.Exception is MessageBusException);
    }

    [Fact]
    public async Task SetAsync_WhenOverlappingWritesReturnOutOfOrder_ReadsLastDistributedValue()
    {
        // Arrange
        const string key = "overlapping-writes";
        using var distributed = new DelayedCache(Log);
        using var cache = CreateCache(distributedCache: distributed);
        distributed.AfterNextWrite(async () => Assert.True(await cache.SetAsync(key, 2)));

        // Act
        Assert.True(await cache.SetAsync(key, 1).WaitAsync(TimeSpan.FromSeconds(5), TestCancellationToken));

        // Assert
        Assert.Equal(2, (await distributed.GetAsync<int>(key)).Value);
        Assert.Equal(2, (await cache.GetAsync<int>(key)).Value);
    }

    [Fact]
    public async Task SetAsync_WhenReplacementExpiresDuringLocalSizing_DoesNotReturnOldValue()
    {
        // Arrange
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var distributedCache = new InMemoryCacheClient(o => o.TimeProvider(clock).LoggerFactory(Log));
        using var messageBus = new InMemoryMessageBus(o => o.TimeProvider(clock).LoggerFactory(Log));
        using var cache = CreateCache(new InMemoryCacheClientOptions
        {
            TimeProvider = clock,
            MaxMemorySize = 1000,
            SizeCalculator = value =>
            {
                if (String.Equals(value as string, "replacement", StringComparison.Ordinal))
                    clock.Advance(TimeSpan.FromSeconds(1));
                return 3;
            }
        }, distributedCache, messageBus);
        const string key = "expired-local-write";
        Assert.True(await cache.SetAsync(key, "old", TimeSpan.FromMinutes(1)));

        // Act
        Assert.True(await cache.SetAsync(key, "replacement", TimeSpan.FromMilliseconds(10)));

        // Assert
        Assert.False((await distributedCache.GetAsync<string>(key)).HasValue);
        Assert.False((await cache.GetAsync<string>(key)).HasValue);
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
    public override Task SetIfHigherAsync_WithLowerOrEqualValue_LeavesValueAndExpirationUnchanged()
    {
        return base.SetIfHigherAsync_WithLowerOrEqualValue_LeavesValueAndExpirationUnchanged();
    }

    [Fact]
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
    public override Task SetIfLowerAsync_WithHigherOrEqualValue_LeavesValueAndExpirationUnchanged()
    {
        return base.SetIfLowerAsync_WithHigherOrEqualValue_LeavesValueAndExpirationUnchanged();
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

    public static MatrixTheoryData<ScalarWrite, bool> ScalarWriteCases =>
        new(Enum.GetValues<ScalarWrite>(), [false, true]);

    [Theory]
    [MemberData(nameof(ScalarWriteCases))]
    public async Task WriteAsync_WhenLocalFallbackExceedsSizeLimit_NextReadReturnsUpdatedValue(ScalarWrite operation, bool shouldThrow)
    {
        // Arrange
        bool rejectLocalWrites = false;
        using var cache = CreateCache(SizeLimitedOptions(value =>
            !rejectLocalWrites || value is 42L ? 3 : throw new InvalidOperationException("Sizing failed"), shouldThrow));
        using var peer = CreateCache();
        const string key = "rejected-local-write";
        Assert.True(await cache.SetAsync(key, 10L));
        Assert.Equal(10L, (await cache.LocalCache.GetAsync<long>(key)).Value);
        Assert.Equal(10L, (await peer.GetAsync<long>(key)).Value);
        if (operation is ScalarWrite.Add)
            Assert.True(await _distributedCache.RemoveAsync(key));
        rejectLocalWrites = true;

        // Act
        Task write = operation switch
        {
            ScalarWrite.Add => cache.AddAsync(key, 15L),
            ScalarWrite.Set => cache.SetAsync(key, 15L),
            ScalarWrite.SetAll => cache.SetAllAsync(new Dictionary<string, long> { [key] = 15L, ["accepted-local-write"] = 42L }),
            ScalarWrite.Replace => cache.ReplaceAsync(key, 15L),
            ScalarWrite.ReplaceIfEqual => cache.ReplaceIfEqualAsync(key, 15L, 10L),
            ScalarWrite.IncrementLong => cache.IncrementAsync(key, 5L),
            ScalarWrite.IncrementDouble => cache.IncrementAsync(key, 5d),
            ScalarWrite.HigherLong => cache.SetIfHigherAsync(key, 15L),
            ScalarWrite.HigherDouble => cache.SetIfHigherAsync(key, 15d),
            ScalarWrite.LowerLong => cache.SetIfLowerAsync(key, 5L),
            ScalarWrite.LowerDouble => cache.SetIfLowerAsync(key, 5d),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

        // Assert
        if (shouldThrow)
        {
            await Assert.ThrowsAsync<MaxEntrySizeExceededCacheException>(() => write);
            Assert.False((await cache.LocalCache.GetAsync<long>(key)).HasValue);
        }
        else
        {
            await write;
        }

        long expected = operation is ScalarWrite.LowerLong or ScalarWrite.LowerDouble ? 5L : 15L;
        Assert.Equal(expected, (await _distributedCache.GetAsync<long>(key)).Value);
        Assert.Equal(expected, (await cache.GetAsync<long>(key)).Value);
        if (operation is not ScalarWrite.Add)
            Assert.Equal(expected, (await peer.GetAsync<long>(key)).Value);
        if (operation is ScalarWrite.SetAll)
            Assert.Equal(42L, (await cache.GetAsync<long>("accepted-local-write")).Value);
    }

    public enum ScalarWrite
    {
        Add,
        Set,
        SetAll,
        Replace,
        ReplaceIfEqual,
        IncrementLong,
        IncrementDouble,
        HigherLong,
        HigherDouble,
        LowerLong,
        LowerDouble
    }

    private HybridCacheClient CreateCache(InMemoryCacheClientOptions? options = null, ICacheClient? distributedCache = null, IMessageBus? messageBus = null)
    {
        if (options is not null)
            options.LoggerFactory = Log;

        return new HybridCacheClient(distributedCache ?? _distributedCache, messageBus ?? _messageBus, options, Log);
    }

    private static InMemoryCacheClientOptions SizeLimitedOptions(Func<object, long> sizeCalculator, bool shouldThrow = true) => new()
    {
        MaxMemorySize = 1000,
        MaxEntrySize = 10,
        ShouldThrowOnMaxEntrySizeExceeded = shouldThrow,
        SizeCalculator = sizeCalculator
    };

    private sealed class DelayedCache(ILoggerFactory loggerFactory, TimeProvider? clock = null) : InMemoryCacheClient(o => o.LoggerFactory(loggerFactory).TimeProvider(clock ?? TimeProvider.System)), ICacheClient
    {
        private Func<Task>? _afterNextRead;
        private Func<Task>? _afterNextWrite;

        public bool RejectExistenceChecks { get; init; }

        public void AfterNextRead(Func<Task> action) => _afterNextRead = action;

        public void AfterNextWrite(Func<Task> action) => _afterNextWrite = action;

        Task<bool> ICacheClient.ExistsAsync(string key) =>
            RejectExistenceChecks ? Task.FromException<bool>(new CacheException("Existence check unavailable")) : ExistsAsync(key);

        Task<IDictionary<string, CacheValue<T>>> ICacheClient.GetAllAsync<T>(IEnumerable<string> keys) =>
            CompleteAsync(GetAllAsync<T>(keys), Interlocked.Exchange(ref _afterNextRead, null));

        Task<CacheValue<T>> ICacheClient.GetAsync<T>(string key) =>
            CompleteAsync(GetAsync<T>(key), Interlocked.Exchange(ref _afterNextRead, null));

        Task<CacheValue<ICollection<T>>> ICacheClient.GetListAsync<T>(string key, int? page, int pageSize) =>
            CompleteAsync(GetListAsync<T>(key, page, pageSize), Interlocked.Exchange(ref _afterNextRead, null));

        Task<long> ICacheClient.ListAddAsync<T>(string key, IEnumerable<T> values, TimeSpan? expiresIn) =>
            CompleteAsync(ListAddAsync(key, values, expiresIn), Interlocked.Exchange(ref _afterNextWrite, null));

        Task<bool> ICacheClient.SetAsync<T>(string key, T value, TimeSpan? expiresIn) =>
            CompleteAsync(SetAsync(key, value, expiresIn), Interlocked.Exchange(ref _afterNextWrite, null));

        private static async Task<T> CompleteAsync<T>(Task<T> operation, Func<Task>? after)
        {
            T result = await operation;
            if (after is not null)
                await after();
            return result;
        }
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
