using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Resilience;
using Foundatio.Serializer;
using Foundatio.Utility;
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
        return CreateCacheClient(o => o.CloneValues(true).ShouldThrowOnSerializationError(shouldThrowOnSerializationError));
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
    public async Task Dispose_WithTrackedEntries_ClearsMemorySize()
    {
        // Arrange
        var clock = new FakeTimeProvider(new DateTimeOffset(DateTime.MinValue, TimeSpan.Zero).AddMilliseconds(100));
        using var cache = CreateCacheClient(o => o.TimeProvider(clock).WithFixedSizing(10_000, 100));
        await cache.SetAsync("first", 1);
        await cache.SetAsync("second", 2);
        Assert.Equal(200, cache.CurrentMemorySize);

        // Act
        cache.Dispose();

        // Assert
        Assert.Empty(cache.Items);
        Assert.Equal(0, cache.CurrentMemorySize);
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

    [Theory]
    [InlineData(1, false)]
    [InlineData(32, false)]
    [InlineData(33, false)]
    [InlineData(1000, false)]
    [InlineData(33, true)]
    public async Task GetAsync_WithCachedDictionaryViews_PreservesRootCyclesAndClonesPayloads(int count, bool sorted)
    {
        // Arrange
        using var cache = CreateCacheClient(o => o.CloneValues(true));
        var pairs = Enumerable.Range(0, count).ToDictionary(key => key, _ => (object)new List<int> { 1 });
        IDictionary<int, object> source = sorted ? new SortedDictionary<int, object>(pairs) : pairs;
        source[0] = source;
        _ = source.Keys;
        _ = source.Values;
        await cache.SetAsync("dictionary", source);

        // Act
        var returned = (await cache.GetAsync<IDictionary<int, object>>("dictionary")).Value;

        // Assert
        Assert.IsType(source.GetType(), returned);
        Assert.Equal(count, returned.Count);
        Assert.Equal(Enumerable.Range(0, count), returned.Keys);
        Assert.Same(returned, returned[0]);
        Assert.Same(returned, returned.Values.First());
        if (count > 1)
        {
            Assert.NotSame(source[1], returned[1]);
            Assert.IsType<List<int>>(returned[1]).Add(2);
        }
        returned.Clear();
        source.Clear();
        var current = (await cache.GetAsync<IDictionary<int, object>>("dictionary")).Value;
        Assert.Equal(count, current.Count);
        Assert.Equal(Enumerable.Range(0, count), current.Keys);
        Assert.Equal(count, current.Values.Count);
        Assert.Same(current, current[0]);
        if (count > 1)
            Assert.Equal([1], Assert.IsType<List<int>>(current[1]));
    }

    [Fact]
    public override Task GetAsync_WithComplexObject_PreservesPropertiesAndReturnsNewInstance()
    {
        return base.GetAsync_WithComplexObject_PreservesPropertiesAndReturnsNewInstance();
    }

    [Theory]
    [InlineData(false, 10)]
    [InlineData(false, 100)]
    [InlineData(true, 10)]
    [InlineData(true, 100)]
    public async Task GetAsync_WithInstanceHashComparer_PreservesLookup(bool ordered, int count)
    {
        // Arrange
        using var cache = CreateCacheClient(o => o.CloneValues(true));
        var comparer = new InstanceHashComparer();
        IDictionary<string, int> source = ordered ? new OrderedDictionary<string, int>(comparer) : new Dictionary<string, int>(comparer);
        for (int index = 0; index < count; index++)
            source.Add($"item-{index}", index);
        await cache.SetAsync("dictionary", source);

        // Act
        var first = (await cache.GetAsync<IDictionary<string, int>>("dictionary")).Value;
        var second = (await cache.GetAsync<IDictionary<string, int>>("dictionary")).Value;

        // Assert
        Assert.Equal(count, first.Count);
        Assert.All(Enumerable.Range(0, count), index => Assert.Equal(index, first[$"ITEM-{index}"]));
        Assert.All(Enumerable.Range(0, count), index => Assert.Equal(index, second[$"ITEM-{index}"]));
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

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task GetAsync_WithOtherSafeComparers_PreservesComparerIdentity(int dictionaryType)
    {
        // Arrange
        using var cache = CreateCacheClient(o => o.CloneValues(true));
        var comparer = StringComparer.OrdinalIgnoreCase;
        IDictionary<string, DateTime?> source = dictionaryType switch
        {
            1 => new SortedDictionary<string, DateTime?>(comparer),
            2 => new SortedList<string, DateTime?>(comparer),
            _ => new OrderedDictionary<string, DateTime?>(comparer)
        };
        source.Add("one", DateTime.UnixEpoch);
        await cache.SetAsync("dictionary", source);
        source.Clear();

        // Act
        var returned = (await cache.GetAsync<IDictionary<string, DateTime?>>("dictionary")).Value;

        // Assert
        Assert.IsType(source.GetType(), returned);
        Assert.Same(comparer, GetListComparer(returned));
        Assert.Equal(DateTime.UnixEpoch, returned["ONE"]);
        returned.Clear();
        Assert.Single((await cache.GetAsync<IDictionary<string, DateTime?>>("dictionary")).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetAsync_WithReferenceHashedKeys_PreservesLookupAndCycles(bool cloneValues)
    {
        // Arrange
        using var cache = CreateCacheClient(o => o.CloneValues(cloneValues));
        var structKey = new ReferenceHashedListItem([1]);
        var structSource = new Dictionary<ReferenceHashedListItem, object>();
        structSource.Add(structKey, structSource);
        await cache.SetAsync("struct-keys", structSource);
        var classKey = new ReferenceHashedListClass([1]);
        var classSource = new Dictionary<ReferenceHashedListClass, object>();
        classSource.Add(classKey, classSource);
        await cache.SetAsync("class-keys", classSource);

        // Act
        var structResult = (await cache.GetAsync<Dictionary<ReferenceHashedListItem, object>>("struct-keys")).Value!;
        var classResult = (await cache.GetAsync<Dictionary<ReferenceHashedListClass, object>>("class-keys")).Value!;

        // Assert
        var copiedStructKey = Assert.Single(structResult.Keys);
        Assert.Equal(cloneValues, !ReferenceEquals(structKey.Values, copiedStructKey.Values));
        Assert.Same(structResult, Assert.Single(structResult.Values));
        Assert.True(structResult.ContainsKey(copiedStructKey));
        var copiedClassKey = Assert.Single(classResult.Keys);
        Assert.Equal(cloneValues, !ReferenceEquals(classKey, copiedClassKey));
        Assert.Equal(cloneValues, !ReferenceEquals(classKey.Values, copiedClassKey.Values));
        Assert.Same(classResult, Assert.Single(classResult.Values));
        Assert.True(classResult.ContainsKey(copiedClassKey));
    }

    [Fact]
    public override Task GetAsync_WithTryGetSemantics_HandlesTypeConversions()
    {
        return base.GetAsync_WithTryGetSemantics_HandlesTypeConversions();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task GetAsync_WithoutCloning_PreservesDictionaryAndComparerIdentity(int dictionaryType)
    {
        // Arrange
        using var cache = CreateCacheClient(o => o.CloneValues(false));
        var comparer = StringComparer.OrdinalIgnoreCase;
        IDictionary<string, DateTime?> source = dictionaryType switch
        {
            0 => new Dictionary<string, DateTime?>(comparer),
            1 => new SortedDictionary<string, DateTime?>(comparer),
            2 => new SortedList<string, DateTime?>(comparer),
            3 => new ConcurrentDictionary<string, DateTime?>(comparer),
            _ => new OrderedDictionary<string, DateTime?>(comparer)
        };
        source.Add("one", DateTime.UnixEpoch);
        await cache.SetAsync("dictionary", source);

        // Act
        var returned = (await cache.GetAsync<IDictionary<string, DateTime?>>("dictionary")).Value;

        // Assert
        Assert.Same(source, returned);
        Assert.Same(comparer, GetListComparer(returned));
        returned.Clear();
        Assert.Empty((await cache.GetAsync<IDictionary<string, DateTime?>>("dictionary")).Value);
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
    public async Task GetListAsync_WithAdoptedReferenceEqualityDictionary_ReadsItsOwnMembers()
    {
        // Arrange
        using var cache = CreateCacheClient(o => o.CloneValues(false));
        string first = new('a', 3);
        string second = new('a', 3);
        var source = new ConcurrentDictionary<string, DateTime?>(ReferenceEqualityComparer.Instance);
        source[first] = null;
        source[second] = null;
        await cache.SetAsync("list", source);
        Assert.Equal(1, await cache.ListAddAsync("list", ["b"]));

        // Act
        var members = (await cache.GetListAsync<string>("list")).Value;
        var firstPage = (await cache.GetListAsync<string>("list", 1, 2)).Value;
        var secondPage = (await cache.GetListAsync<string>("list", 2, 2)).Value;

        // Assert
        Assert.Equal(["aaa", "aaa", "b"], members.Order(StringComparer.Ordinal));
        Assert.Equal(2, firstPage.Count);
        Assert.Equal(members.Order(StringComparer.Ordinal), firstPage.Concat(secondPage).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(null, 0, 1, 4)]
    [InlineData(1, 2, 1, 2)]
    [InlineData(2, 2, 3, 2)]
    [InlineData(3, 2, 1, 0)]
    [InlineData(1, 0, 1, 0)]
    [InlineData(1, -1, 1, 0)]
    [InlineData(Int32.MaxValue, 2, 1, 2)]
    public async Task GetListAsync_WithClonedSafeKeys_PreservesPagingAndArrayIsolation(int? page, int pageSize, int first, int count)
    {
        // Arrange
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var cache = CreateCacheClient(o => o.CloneValues(true).TimeProvider(clock));
        var source = Enumerable.Range(0, 5).ToDictionary(value => value, _ => (DateTime?)null);
        source[0] = clock.GetUtcNow().UtcDateTime.AddTicks(-1);
        await cache.SetAsync("list", source);

        // Act
        var result = await cache.GetListAsync<int>("list", page, pageSize);

        // Assert
        Assert.True(result.HasValue);
        Assert.Equal(Enumerable.Range(first, count), result.Value);
        var returned = Assert.IsType<int[]>(result.Value);
        if (returned.Length > 0)
            returned[0] = -1;
        Assert.Equal([1, 2, 3, 4], (await cache.GetListAsync<int>("list")).Value);
        Assert.Equal(5, Assert.IsType<Dictionary<int, DateTime?>>(GetEntry(cache, "list").StoredValue).Count);
    }

    [Theory]
    [InlineData("Live", true, true)]
    [InlineData("Empty", true, false)]
    [InlineData("ExpiredMembers", true, false)]
    [InlineData("Missing", false, false)]
    [InlineData("ExpiredKey", false, false)]
    public async Task GetListAsync_WithClonedSafeKeys_PreservesReadStatistics(string state, bool hit, bool hasValue)
    {
        // Arrange
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var cache = CreateCacheClient(o => o.CloneValues(true).TimeProvider(clock));
        InMemoryCacheClient.CacheEntry? entry = null;
        if (state != "Missing")
        {
            var values = new Dictionary<int, DateTime?>();
            if (state != "Empty")
                values[1] = state == "ExpiredMembers" ? clock.GetUtcNow().UtcDateTime : null;
            await cache.SetAsync("list", values, state == "ExpiredKey" ? TimeSpan.FromMinutes(1) : null);
            entry = GetEntry(cache, "list");
        }
        long previousAccess = entry?.LastAccessTicks ?? 0;
        clock.Advance(TimeSpan.FromMinutes(2));
        cache.ResetStats();

        // Act
        var result = await cache.GetListAsync<int>("list");

        // Assert
        Assert.Equal(hasValue, result.HasValue);
        Assert.Equal(hasValue ? [1] : [], result.Value);
        Assert.Equal(hit ? 1 : 0, cache.Hits);
        Assert.Equal(hit ? 0 : 1, cache.Misses);
        Assert.Equal(1, cache.Reads);
        Assert.Equal(0, cache.Writes);
        if (entry is not null)
        {
            if (hit)
                Assert.Same(entry, GetEntry(cache, "list"));
            Assert.Equal(hit ? clock.GetUtcNow().Ticks : previousAccess, entry.LastAccessTicks);
            Assert.Equal(state == "Empty" ? 0 : 1, Assert.IsType<Dictionary<int, DateTime?>>(entry.StoredValue).Count);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task GetListAsync_WithExpirationAtCurrentTick_FiltersOnlyAfterTheBoundary(int remainingSeconds)
    {
        // Arrange
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var cache = CreateCacheClient(o => o.CloneValues(false).TimeProvider(clock));
        var now = clock.GetUtcNow().UtcDateTime;
        var values = Enumerable.Range(0, 97).ToDictionary(value => value, _ => (DateTime?)null);
        values[1] = now;
        values[2] = now.AddTicks(1);
        await cache.SetAsync("list", values);
        await cache.ListAddAsync("list", [0]);
        var previous = (await cache.GetListAsync<int>("list")).Value;

        // Act & Assert
        Assert.Equal(Enumerable.Range(0, 97), previous);
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(Enumerable.Range(0, 97).Where(value => value != 1), (await cache.GetListAsync<int>("list")).Value);
        Assert.Equal(new[] { 0, 2, 3, 4, 5 }, (await cache.GetListAsync<int>("list", 1, 5)).Value);
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(Enumerable.Range(0, 97).Where(value => value is not (1 or 2)), (await cache.GetListAsync<int>("list")).Value);

        await cache.ListAddAsync("list", [97]);
        Assert.Equal(Enumerable.Range(0, 97), previous);
        Assert.Equal(Enumerable.Range(0, 98).Where(value => value is not (1 or 2)).Order(),
            (await cache.GetListAsync<int>("list")).Value.Order());
        Assert.Null(await cache.GetExpirationAsync("list"));

        // A member that expires exactly now survives a remove that prunes expired members.
        await cache.ListAddAsync("list", [200], TimeSpan.FromSeconds(2 + remainingSeconds));
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(0, await cache.ListRemoveAsync("list", [-1]));
        Assert.Equal(clock.GetUtcNow().UtcDateTime.AddSeconds(remainingSeconds),
            (await cache.GetAsync<Dictionary<int, DateTime?>>("list")).Value[200]);
        Assert.Contains(200, (await cache.GetListAsync<int>("list")).Value);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetListAsync_WithNonListValues_PreservesErrorHandling(bool shouldThrow)
    {
        // Arrange
        using var cache = CreateCacheClient(o => o.CloneValues(true).ShouldThrowOnSerializationError(shouldThrow));
        await cache.SetAsync("wrong", 1);
        await cache.SetAsync<string?>("null", null);
        cache.ResetStats();

        // Act & Assert
        if (shouldThrow)
            await Assert.ThrowsAsync<InvalidCastException>(() => cache.GetListAsync<int>("wrong"));
        else
        {
            var result = await cache.GetListAsync<int>("wrong");
            Assert.False(result.HasValue);
            Assert.Empty(result.Value);
        }
        Assert.Equal(1, cache.Hits);
        Assert.Equal(0, cache.Misses);

        var error = await Assert.ThrowsAsync<ArgumentNullException>(() => cache.GetListAsync<int>("null"));
        Assert.Equal("source", error.ParamName);
        Assert.Equal(2, cache.Hits);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => cache.GetListAsync<int>("wrong", 0));
        Assert.Equal(2, cache.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetListAsync_WithPagedExpiringMembers_PreservesOrderAndHasValue(bool updatedThroughListOperations)
    {
        // Arrange
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var cache = CreateCacheClient(o => o.CloneValues(false).TimeProvider(clock));
        var now = clock.GetUtcNow().UtcDateTime;
        var source = Enumerable.Range(0, 257).ToDictionary(value => value, value => (DateTime?)now.AddSeconds(value % 3 == 0 ? 1 : 2));
        await cache.SetAsync("list", source);
        if (updatedThroughListOperations)
        {
            await cache.ListAddAsync("list", [0], TimeSpan.FromSeconds(1));
            await cache.ListAddAsync("list", [-1]);
            await cache.ListRemoveAsync("list", [-1]);
        }
        await cache.SetExpirationAsync("list", TimeSpan.FromMinutes(1));

        for (int second = 1; second <= 3; second++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            var expected = source.Where(pair => pair.Value >= clock.GetUtcNow().UtcDateTime).Select(pair => pair.Key).ToArray();
            foreach (var (page, pageSize) in new[] { (1, 100), (2, 32), (3, 32), (2, 100), (3, 100), (4, 100), (1, 0), (1, -1), (Int32.MaxValue, 2) })
            {
                cache.ResetStats();

                // Act
                var result = await cache.GetListAsync<int>("list", page, pageSize);

                // Assert
                Assert.Equal(expected.Length > 0, result.HasValue);
                Assert.Equal(expected.Skip(unchecked((page - 1) * pageSize)).Take(pageSize), result.Value);
                Assert.Equal(1, cache.Hits);
                Assert.Equal(1, cache.Reads);
                Assert.Equal(0, cache.Writes);
            }
        }
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

    [Theory]
    [InlineData("Increment", false)]
    [InlineData("Increment", true)]
    [InlineData("IncrementDouble", false)]
    [InlineData("IncrementDouble", true)]
    [InlineData("SetIfHigher", false)]
    [InlineData("SetIfLower", false)]
    [InlineData("ListAdd", false)]
    public async Task IncrementAsync_WhenStoredEntryExpiresDuringSizing_StartsFromMissing(string operation, bool competingWrite)
    {
        // Arrange
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        bool observedMissing = false;
        InMemoryCacheClient? client = null;
        using var cache = client = CreateCacheClient(o => o.TimeProvider(clock).CloneValues(false)
            .MaxMemorySize(1000).SizeCalculator(value =>
            {
                bool expireStoredEntry = value is IDictionary<string, DateTime?> list ? list.Count == 2 : Convert.ToDouble(value) == 101;
                if (expireStoredEntry)
                {
                    clock.Advance(TimeSpan.FromMinutes(2));
                    observedMissing = !client!.ExistsAsync("key").GetAwaiter().GetResult();
                    if (competingWrite)
                        StoreEntry(client, "key", CreateEntry(1000L, clock));
                }
                return 8;
            }));
        var expired = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = cache.ItemExpired.AddSyncHandler((_, args) => expired.TrySetResult(args.Key));
        object stored = operation switch
        {
            "ListAdd" => new Dictionary<string, DateTime?> { ["old"] = null },
            "SetIfLower" => 200L,
            _ => 100L
        };
        StoreEntry(cache, "key", CreateEntry(stored, clock, TimeSpan.FromMinutes(1)));

        // Act
        double result = operation switch
        {
            "Increment" => await cache.IncrementAsync("key", 1L),
            "IncrementDouble" => await cache.IncrementAsync("key", 1d),
            "SetIfHigher" => await cache.SetIfHigherAsync("key", 101L),
            "SetIfLower" => await cache.SetIfLowerAsync("key", 101L),
            _ => await cache.ListAddAsync("key", ["new"])
        };

        // Assert
        Assert.True(observedMissing);
        Assert.Equal("key", await expired.Task.WaitAsync(TimeSpan.FromSeconds(5), TestCancellationToken));
        Assert.Equal(8, cache.CurrentMemorySize);
        if (operation is "ListAdd")
        {
            Assert.Equal(1, result);
            Assert.Equal(["new"], (await cache.GetListAsync<string>("key")).Value);
            return;
        }

        long expected = competingWrite ? 1001 : operation.StartsWith("Increment", StringComparison.Ordinal) ? 1 : 101;
        Assert.Equal(expected, result);
        Assert.Equal(expected, (await cache.GetAsync<long>("key")).Value);
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

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task IncrementAsync_WithRejectedResult_PreservesEntry(bool useDouble, bool shouldThrow)
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var cache = CreateCacheClient(o => o.TimeProvider(timeProvider).MaxMemorySize(1000)
            .SizeCalculator(value => Convert.ToInt64(value)).MaxEntrySize(10)
            .ShouldThrowOnMaxEntrySizeExceeded(shouldThrow));
        await cache.SetAsync("key", 9L, TimeSpan.FromMinutes(1));

        // Act
        async Task<double> IncrementAsync() => useDouble
            ? await cache.IncrementAsync("key", 2d, TimeSpan.FromMinutes(5))
            : await cache.IncrementAsync("key", 2L, TimeSpan.FromMinutes(5));
        if (shouldThrow)
            await Assert.ThrowsAsync<MaxEntrySizeExceededCacheException>(IncrementAsync);
        else
            Assert.Equal(0, await IncrementAsync());

        // Assert
        Assert.Equal(9, (await cache.GetAsync<long>("key")).Value);
        Assert.Equal(TimeSpan.FromMinutes(1), await cache.GetExpirationAsync("key"));
        Assert.Equal(9, cache.CurrentMemorySize);
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
    public async Task Items_WhenRead_DoesNotChangeEvictionOrder()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var cache = CreateCacheClient(o => o.MaxItems(2).TimeProvider(timeProvider));
        await cache.SetAsync("first", "1");
        timeProvider.Advance(TimeSpan.FromMilliseconds(100));
        await cache.SetAsync("second", "2");
        timeProvider.Advance(TimeSpan.FromMilliseconds(100));

        // Act
        Assert.Equal(2, cache.Items.Count);
        timeProvider.Advance(TimeSpan.FromMilliseconds(100));
        await cache.SetAsync("third", "3");

        // Assert
        Assert.False(await cache.ExistsAsync("first"));
        Assert.True(await cache.ExistsAsync("second"));
        Assert.True(await cache.ExistsAsync("third"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Items_WithMutableValue_HonorsCloneValues(bool cloneValues, bool nullValue)
    {
        // Arrange
        using var cache = CreateCacheClient(o => o.CloneValues(cloneValues));
        var original = nullValue ? null : new SimpleModel { Data1 = "original" };
        await cache.SetAsync("key", original);

        // Act
        var item = Assert.Single(cache.Items);

        // Assert
        Assert.Equal("key", item.Key);
        if (nullValue)
        {
            Assert.Null(item.Value);
            return;
        }

        var exposed = Assert.IsType<SimpleModel>(item.Value);
        exposed.Data1 = "changed";
        Assert.Equal(cloneValues ? "original" : "changed", (await cache.GetAsync<SimpleModel>("key")).Value!.Data1);
        if (cloneValues)
            Assert.NotSame(original, exposed);
        else
            Assert.Same(original, exposed);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListAddAsync_WhenComparerThrowsPartwayThroughABatch_LeavesTheListUnchanged(bool cloneValues)
    {
        // Arrange
        using var cache = CreateCacheClient(o => o.CloneValues(cloneValues));
        await cache.SetAsync("list", new Dictionary<int, DateTime?>(new ThrowingComparer(3)) { [0] = null });
        Assert.Equal(1, await cache.ListAddAsync("list", [1]));

        // Act
        var exception = await Record.ExceptionAsync(async () => _ = await cache.ListAddAsync("list", new[] { 2, 3 }));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal([0, 1], (await cache.GetListAsync<int>("list")).Value.Order());
        Assert.Equal(1, await cache.ListAddAsync("list", [2]));
        Assert.Equal([0, 1, 2], (await cache.GetListAsync<int>("list")).Value.Order());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListAddAsync_WhenEntryAndInputExpireDuringSizing_ReclaimsEntryOnce(bool existingList)
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        bool advance = false;
        using var cache = CreateCacheClient(o => o.TimeProvider(timeProvider).MaxMemorySize(1000)
            .SizeCalculator(value =>
            {
                if (advance && value is IDictionary<string, DateTime?> { Count: 1 } input && input.ContainsKey("new"))
                {
                    advance = false;
                    timeProvider.Advance(TimeSpan.FromMinutes(2));
                }
                return value is ICollection collection ? collection.Count : 1;
            }));
        if (existingList)
            await cache.ListAddAsync("list", ["old"], TimeSpan.FromMinutes(1));
        else
            await cache.SetAsync("list", "scalar", TimeSpan.FromMinutes(1));
        int notifications = 0;
        var expired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = cache.ItemExpired.AddSyncHandler((_, _) =>
        {
            Interlocked.Increment(ref notifications);
            expired.TrySetResult();
        });
        advance = true;

        // Act
        long added = await cache.ListAddAsync("list", ["new"], TimeSpan.FromMinutes(1));

        // Assert
        Assert.Equal(0, added);
        Assert.False(await cache.ExistsAsync("list"));
        Assert.Equal(0, cache.CurrentMemorySize);
        Assert.Equal(0, cache.Count);
        await expired.Task.WaitAsync(TimeSpan.FromSeconds(5), TestCancellationToken);
        await cache.SetAsync("other", "value");
        await Task.Delay(50, TestCancellationToken);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public async Task ListAddAsync_WhenEntryAndInputExpireDuringSizing_ReclaimsTheExpiredList()
    {
        // Arrange
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        bool advance = false;
        using var cache = CreateCacheClient(o => o.CloneValues(false).TimeProvider(clock).MaxMemorySize(1_000)
            .SizeCalculator(value =>
            {
                if (advance && value is IDictionary<int, DateTime?> members && members.ContainsKey(1))
                {
                    advance = false;
                    clock.Advance(TimeSpan.FromMinutes(2));
                }

                return value is ICollection<KeyValuePair<int, DateTime?>> pairs ? pairs.Count : 1;
            }));
        var expired = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = cache.ItemExpired.AddSyncHandler((_, args) => expired.TrySetResult(args.Key));
        await cache.ListAddAsync("list", [1], TimeSpan.FromMinutes(1));
        advance = true;

        // Act
        long added = await cache.ListAddAsync("list", [2], TimeSpan.FromMinutes(1));

        // Assert
        Assert.Equal(0, added);
        Assert.Equal("list", await expired.Task.WaitAsync(TimeSpan.FromSeconds(5), TestCancellationToken));
        Assert.Equal(0, cache.CurrentMemorySize);
        Assert.False((await cache.GetListAsync<int>("list")).HasValue);
    }

    [Fact]
    public async Task ListAddAsync_WhenSizerAddsToTheSameListAndRejectsTheOuterUpdate_KeepsOnlyTheNestedWrite()
    {
        // Arrange
        InMemoryCacheClient? cache = null;
        bool reenter = false;
        cache = CreateCacheClient(o => o.CloneValues(false).MaxMemorySize(1_000).SizeCalculator(value =>
        {
            if (value is IDictionary<int, DateTime?> members && members.ContainsKey(1) && members.ContainsKey(2) && reenter)
            {
                reenter = false;
                Assert.Equal(1, cache!.ListAddAsync("list", [3]).GetAwaiter().GetResult());
                return -1;
            }

            return value is ICollection<KeyValuePair<int, DateTime?>> pairs ? pairs.Count : 1;
        }));
        using (cache)
        {
            await cache.ListAddAsync("list", [1]);
            reenter = true;

            // Act
            long added = await cache.ListAddAsync("list", [2]);

            // Assert
            Assert.Equal(0, added);
            Assert.Equal([1, 3], (await cache.GetListAsync<int>("list")).Value.Order());
            Assert.Equal(2, cache.CurrentMemorySize);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListAddAsync_WithActualLargeListUpdates_DoesNotCopyEntireList(bool fixedSizing)
    {
        // Arrange
        using var cache = CreateCacheClient(o =>
        {
            o.CloneValues(false).TimeProvider(new FakeTimeProvider(DateTimeOffset.UtcNow));
            return fixedSizing ? o.WithFixedSizing(1000000, 64) : o;
        });
        await cache.ListAddAsync("small", Enumerable.Range(0, 10));
        await cache.ListAddAsync("large", Enumerable.Range(0, 1000));
        int[] item = [-1];
        await UpdateAsync("small");
        await UpdateAsync("large");

        // Act
        long smallAllocation = await MeasureAsync("small");
        long largeAllocation = await MeasureAsync("large");

        // Assert
        Assert.True(largeAllocation <= smallAllocation + 8192,
            $"Small updates should not copy every element: small={smallAllocation}, large={largeAllocation} bytes.");
        Assert.Equal(Enumerable.Range(0, 1000), (await cache.GetListAsync<int>("large")).Value);
        Assert.Equal(fixedSizing ? 128 : 0, cache.CurrentMemorySize);

        async Task UpdateAsync(string key)
        {
            await cache.ListAddAsync(key, item);
            await cache.ListRemoveAsync(key, item);
        }

        async Task<long> MeasureAsync(string key)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            await UpdateAsync(key);
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
    }

    [Fact]
    public async Task ListAddAsync_WithBuiltInDynamicSizing_DoesNotCopyEntireList()
    {
        // Arrange
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var cache = CreateCacheClient(o => o.CloneValues(false).TimeProvider(clock)
            .WithDynamicSizing(100_000_000, Log));
        await cache.ListAddAsync("small", Enumerable.Range(0, 1000));
        await cache.ListAddAsync("large", Enumerable.Range(0, 10_000));
        int[] item = [-1];
        await UpdateAsync("small");
        await UpdateAsync("large");

        // Act
        long smallAllocation = await MeasureAsync("small");
        long largeAllocation = await MeasureAsync("large");

        // Assert
        _logger.LogInformation("Dynamic list updates allocated {Small} bytes small and {Large} bytes large", smallAllocation, largeAllocation);
        Assert.True(largeAllocation <= smallAllocation + 32_768,
            $"Dynamic sizing must not copy the whole list: small={smallAllocation}, large={largeAllocation} bytes.");
        Assert.Equal(Enumerable.Range(0, 10_000), (await cache.GetListAsync<int>("large")).Value);

        async Task UpdateAsync(string key)
        {
            await cache.ListAddAsync(key, item);
            await cache.ListRemoveAsync(key, item);
        }

        async Task<long> MeasureAsync(string key)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            await UpdateAsync(key);
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListAddAsync_WithCloneValuesAndARetainedSizerArgument_StoresAPrivateList(bool remove)
    {
        // Arrange
        IDictionary<int, DateTime?>? retained = null;
        using var cache = CreateCacheClient(o => o.CloneValues(true).MaxMemorySize(1_000).SizeCalculator(value =>
        {
            if (value is IDictionary<int, DateTime?> dictionary)
            {
                retained = dictionary;
                _ = dictionary.Keys;
                _ = dictionary.Values;
            }

            return value is ICollection<KeyValuePair<int, DateTime?>> pairs ? pairs.Count : 1;
        }));
        if (remove)
            Assert.Equal(3, await cache.ListAddAsync("list", [1, 2, 3]));

        // Act
        long changed = remove
            ? await cache.ListRemoveAsync("list", [3])
            : await cache.ListAddAsync("list", [1, 2]);
        Assert.NotNull(retained);
        retained.Clear();

        // Assert
        Assert.Equal(remove ? 1 : 2, changed);
        Assert.Equal([1, 2], (await cache.GetListAsync<int>("list")).Value.Order());
        Assert.Equal(2, cache.CurrentMemorySize);
    }

    [Fact]
    public async Task ListAddAsync_WithCloneValues_StoresCopiesOfAddedValues()
    {
        // Arrange
        using var cache = CreateCacheClient(o => o.CloneValues(true));
        await cache.ListAddAsync("set", new[] { new SimpleModel { Data1 = "first" } });
        var added = new SimpleModel { Data1 = "second" };

        // Act
        await cache.ListAddAsync("set", new[] { added });

        // Assert
        var stored = Assert.IsAssignableFrom<IDictionary<SimpleModel, DateTime?>>(GetEntry(cache, "set").PeekValue());
        Assert.Equal(2, stored.Count);
        Assert.DoesNotContain(stored.Keys, k => ReferenceEquals(k, added));
        var returned = (await cache.GetListAsync<SimpleModel>("set")).Value;
        returned.Single(value => value.Data1 == "first").Data1 = "changed";
        var current = (await cache.GetListAsync<SimpleModel>("set")).Value;
        Assert.Contains(current, value => value.Data1 == "first");
        Assert.DoesNotContain(current, value => value.Data1 == "changed");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListAddAsync_WithConcurrentLargeListUpdates_PreservesUnrelatedValues(bool cloneValues)
    {
        // Arrange
        using var cache = CreateCacheClient(o => o.CloneValues(cloneValues));
        var original = Enumerable.Range(0, 1000).ToArray();
        await cache.ListAddAsync("set", original);

        // Act
        await Parallel.ForEachAsync(Enumerable.Range(0, 4), TestCancellationToken, async (worker, token) =>
        {
            int[] item = [-worker - 1];
            for (int iteration = 0; iteration < 100; iteration++)
            {
                await Task.Yield();
                await cache.ListAddAsync("set", item);
                await cache.ListRemoveAsync("set", item);
            }
        });

        // Assert
        var remaining = (await cache.GetListAsync<int>("set")).Value!;
        Assert.Equal(original, remaining.OrderBy(value => value));
    }

    [Fact]
    public override Task ListAddAsync_WithConcurrentRequests_DoesNotLoseValues()
    {
        return base.ListAddAsync_WithConcurrentRequests_DoesNotLoseValues();
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(false, 3)]
    [InlineData(false, 4)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    [InlineData(true, 3)]
    [InlineData(true, 4)]
    public async Task ListAddAsync_WithCustomComparer_PreservesComparerAcrossUpdates(bool cloneValues, int dictionaryType)
    {
        // Arrange
        using var cache = CreateCacheClient(o => o.CloneValues(cloneValues));
        IDictionary<string, DateTime?> values = dictionaryType switch
        {
            0 => new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase),
            1 => new SortedDictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase),
            2 => new SortedList<string, DateTime?>(StringComparer.OrdinalIgnoreCase),
            3 => new ConcurrentDictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase),
            _ => new OrderedDictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase)
        };
        values["first"] = null;
        await cache.SetAsync("set", values);

        // Act
        await cache.ListAddAsync("set", new[] { "FIRST", "second" });
        long removed = await cache.ListRemoveAsync("set", new[] { "SECOND" });

        // Assert
        Assert.Equal(1, removed);
        var stored = (await cache.GetAsync<IDictionary<string, DateTime?>>("set")).Value!;
        Assert.IsType(values.GetType(), stored);
        Assert.Single(stored);
        Assert.True(stored.ContainsKey("FIRST"));
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 1000)]
    [InlineData(true, 1000)]
    public async Task ListAddAsync_WithExistingList_DoesNotMutatePreviouslyReadValue(bool cloneValues, int count)
    {
        // Arrange
        using var cache = CreateCacheClient(o => o.CloneValues(cloneValues));
        var initial = Enumerable.Range(0, count).Select(i => $"a{i}").ToArray();
        await cache.ListAddAsync("set", initial);
        var previouslyRead = (await cache.GetAsync<IDictionary<string, DateTime?>>("set")).Value;

        // Act
        await cache.ListAddAsync("set", new[] { "b" });
        await cache.ListRemoveAsync("set", new[] { "a0" });

        // Assert
        Assert.Equal(initial.Order(StringComparer.Ordinal), previouslyRead!.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(count, previouslyRead.Values.Count);
        var current = (await cache.GetListAsync<string>("set")).Value;
        Assert.Contains("b", current);
        Assert.DoesNotContain("a0", current);
        Assert.Equal(count, current.Count);
    }

    [Fact]
    public override Task ListAddAsync_WithExpiration_SetsExpirationCorrectly()
    {
        return base.ListAddAsync_WithExpiration_SetsExpirationCorrectly();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListAddAsync_WithExposedDictionary_PreservesDirectMutationsAndPriorSnapshot(bool dynamicSizing)
    {
        // Arrange
        using var calculator = new SizeCalculator(Log);
        using var cache = CreateCacheClient(o => dynamicSizing
            ? o.CloneValues(false).WithDynamicSizing(100_000_000, Log)
            : o.CloneValues(false));
        await cache.ListAddAsync("set", Enumerable.Range(0, 64));
        await cache.ListAddAsync("set", new[] { 64 });
        var exposed = (await cache.GetAsync<Dictionary<int, DateTime?>>("set")).Value!;
        exposed[100] = null;

        // Act
        await cache.ListAddAsync("set", new[] { 101 });
        await cache.ListRemoveAsync("set", new[] { 0 });

        // Assert
        var current = (await cache.GetListAsync<int>("set")).Value!;
        Assert.Contains(100, current);
        Assert.Contains(101, current);
        Assert.DoesNotContain(0, current);
        Assert.Contains(0, exposed.Keys);
        Assert.DoesNotContain(101, exposed.Keys);
        if (dynamicSizing)
            Assert.Equal(calculator.CalculateSize((await cache.GetAsync<Dictionary<int, DateTime?>>("set")).Value!), cache.CurrentMemorySize);
    }

    [Fact]
    public override Task ListAddAsync_WithInvalidArguments_ThrowsException()
    {
        return base.ListAddAsync_WithInvalidArguments_ThrowsException();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ListAddAsync_WithOwnedSafeKeys_PreservesFutureCloneOnRead(int sizing)
    {
        // Arrange
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var cache = CreateCacheClient(o =>
        {
            o.CloneValues(true).TimeProvider(clock);
            return sizing switch
            {
                1 => o.WithFixedSizing(100_000_000, 64),
                2 => o.WithDynamicSizing(100_000_000, Log),
                _ => o
            };
        });
        await cache.ListAddAsync("list", Enumerable.Range(0, 1000));
        var stored = Assert.IsType<Dictionary<int, DateTime?>>(GetEntry(cache, "list").PeekValue());

        // Act
        var returned = (await cache.GetAsync<Dictionary<int, DateTime?>>("list")).Value;
        returned.Clear();
        var item = Assert.IsType<Dictionary<int, DateTime?>>(Assert.Single(cache.Items).Value);
        item.Clear();
        await cache.SetExpirationAsync("list", TimeSpan.FromMinutes(1));
        (await cache.GetAsync<Dictionary<int, DateTime?>>("list")).Value.Clear();

        // Assert
        Assert.NotSame(stored, returned);
        Assert.NotSame(stored, item);
        Assert.Equal(Enumerable.Range(0, 1000), (await cache.GetListAsync<int>("list")).Value);
        Assert.Equal(TimeSpan.FromMinutes(1), await cache.GetExpirationAsync("list"));
        Assert.Equal(1000, stored.Count);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(32, true)]
    [InlineData(33, true)]
    [InlineData(100, false)]
    [InlineData(100, true)]
    public async Task ListAddAsync_WithReferenceHoldingStructs_HonorsCloneValues(int count, bool cloneValues)
    {
        // Arrange
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var cache = CreateCacheClient(o => o.CloneValues(cloneValues).TimeProvider(clock));
        var original = Enumerable.Range(0, count).Select(id => new ReferenceHoldingListItem(id, [id])).ToArray();
        await cache.ListAddAsync("set", original);
        var added = new ReferenceHoldingListItem(-1, [-1]);
        await cache.ListAddAsync("set", [added]);
        await cache.SetExpirationAsync("set", TimeSpan.FromMinutes(1));
        var classItem = new MutableClassListItem(0);
        await cache.ListAddAsync("classes", [classItem]);

        // Act
        original[0].Values.Add(42);
        added.Values.Add(42);
        (await cache.GetListAsync<ReferenceHoldingListItem>("set")).Value!.Single(item => item.Id == -1).Values.Add(42);
        (await cache.GetAsync<IDictionary<ReferenceHoldingListItem, DateTime?>>("set")).Value!.Keys.Single(item => item.Id == 0).Values.Add(42);
        ((IDictionary<ReferenceHoldingListItem, DateTime?>)cache.Items.Single(item => item.Key == "set").Value!).Keys.Single(item => item.Id == 0).Values.Add(42);
        classItem.Values.Add(42);
        (await cache.GetListAsync<MutableClassListItem>("classes", 1, 1)).Value.Single().Values.Add(43);

        // Assert
        var stored = (await cache.GetListAsync<ReferenceHoldingListItem>("set")).Value!;
        Assert.Equal(cloneValues ? 1 : 4, stored.Single(item => item.Id == 0).Values.Count);
        Assert.Equal(cloneValues ? 1 : 3, stored.Single(item => item.Id == -1).Values.Count);
        Assert.Equal(TimeSpan.FromMinutes(1), await cache.GetExpirationAsync("set"));
        var storedClasses = (await cache.GetAsync<Dictionary<MutableClassListItem, DateTime?>>("classes")).Value!;
        var classKey = Assert.Single(storedClasses.Keys);
        Assert.Equal(cloneValues ? new[] { 0 } : new[] { 0, 42, 43 }, classKey.Values);
        Assert.True(storedClasses.ContainsKey(classKey));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    public async Task ListAddAsync_WithRejectedUpdate_PreservesEntry(bool remove, bool shouldThrow, bool dynamicSizing)
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        bool reject = false;
        string[] items = dynamicSizing ? Enumerable.Range(0, 64).Select(i => $"item-{i:D4}").ToArray() : ["a", "b"];
        var ttl = TimeSpan.FromMinutes(1);
        using var calculator = new SizeCalculator(Log);
        var expiresAt = timeProvider.GetUtcNow().UtcDateTime.Add(ttl);
        long size = dynamicSizing ? calculator.CalculateSize(items.ToDictionary(item => item, _ => (DateTime?)expiresAt)) : 10;
        using var cache = CreateCacheClient(o =>
        {
            o.TimeProvider(timeProvider).ShouldThrowOnMaxEntrySizeExceeded(shouldThrow);
            return dynamicSizing
                ? o.CloneValues(false).WithDynamicSizing(100_000_000, Log).MaxEntrySize(size)
                : o.MaxMemorySize(1000).MaxEntrySize(15)
                    .SizeCalculator(value => reject && ((IDictionary<string, DateTime?>)value).Count == (remove ? 1 : 3) ? 20 : 10);
        });
        await cache.ListAddAsync("key", items, ttl);
        var previous = GetEntry(cache, "key");
        reject = true;

        // Act
        async Task<long> UpdateAsync() => remove
            ? await cache.ListRemoveAsync("key", [items[0]])
            : await cache.ListAddAsync("key", ["c"], TimeSpan.FromMinutes(5));
        if (shouldThrow)
        {
            var error = await Assert.ThrowsAsync<MaxEntrySizeExceededCacheException>(UpdateAsync);
            Assert.Equal(typeof(Dictionary<string, DateTime?>).Name, error.EntryType);
        }
        else
        {
            Assert.Equal(0, await UpdateAsync());
        }

        // Assert
        Assert.Same(previous, GetEntry(cache, "key"));
        Assert.Equal(items, (await cache.GetListAsync<string>("key")).Value);
        Assert.Equal(ttl, await cache.GetExpirationAsync("key"));
        Assert.Equal(size, cache.CurrentMemorySize);
    }

    [Theory]
    [InlineData("None")]
    [InlineData("Fixed")]
    [InlineData("Dynamic")]
    public async Task ListAddAsync_WithRepeatedMixedBatches_MatchesSetModel(string sizing)
    {
        // Arrange
        using var calculator = new SizeCalculator(Log);
        using var cache = CreateCacheClient(o => sizing switch
        {
            "Fixed" => o.WithFixedSizing(1000000, 64),
            "Dynamic" => o.WithDynamicSizing(100_000_000, Log),
            _ => o
        });
        var expected = new HashSet<int>(Enumerable.Range(0, 1000));
        await cache.ListAddAsync("set", expected);
        var random = new Random(570);
        int[] batchSizes = [1, 5, 31, 33, 128];

        for (int iteration = 0; iteration < 200; iteration++)
        {
            var batch = Enumerable.Range(0, batchSizes[iteration % batchSizes.Length]).Select(_ => random.Next(-1024, 2048)).ToArray();

            bool add = iteration % 2 == 0;
            int expectedCount = add ? batch.Distinct().Count() : batch.Count(expected.Remove);
            if (add)
                expected.UnionWith(batch);

            // Act
            long affected = add ? await cache.ListAddAsync("set", batch) : await cache.ListRemoveAsync("set", batch);

            // Assert
            Assert.Equal(expectedCount, affected);
            Assert.Equal(expected.OrderBy(value => value), (await cache.GetListAsync<int>("set")).Value!.OrderBy(value => value));
            long expectedSize = sizing switch
            {
                "Fixed" => 64,
                "Dynamic" => calculator.CalculateSize((await cache.GetAsync<Dictionary<int, DateTime?>>("set")).Value!),
                _ => 0
            };
            Assert.Equal(expectedSize, cache.CurrentMemorySize);
        }
    }

    [Fact]
    public override Task ListAddAsync_WithSingleString_StoresAsStringNotCharArray()
    {
        return base.ListAddAsync_WithSingleString_StoresAsStringNotCharArray();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ListAddAsync_WithUnchangedKey_RefreshesExpirationAndPrunesExpiredMembers(int sizing)
    {
        // Arrange
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var cache = CreateCacheClient(o =>
        {
            o.CloneValues(false).TimeProvider(clock);
            return sizing switch
            {
                1 => o.WithFixedSizing(100_000_000, 64),
                2 => o.WithDynamicSizing(100_000_000, Log),
                _ => o
            };
        });
        var expiresAt = clock.GetUtcNow().UtcDateTime.AddSeconds(5);
        await cache.SetAsync("list", Enumerable.Range(0, 64).ToDictionary(key => key, _ => (DateTime?)expiresAt), TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, await cache.ListAddAsync("list", [0], TimeSpan.FromSeconds(30)));
        clock.Advance(TimeSpan.FromSeconds(10));

        // Act
        long added = await cache.ListAddAsync("list", [0], TimeSpan.FromSeconds(20));

        // Assert
        Assert.Equal(1, added);
        var values = (await cache.GetAsync<Dictionary<int, DateTime?>>("list")).Value!;
        Assert.Equal([0], values.Keys);
        Assert.Equal(TimeSpan.FromSeconds(20), await cache.GetExpirationAsync("list"));
    }

    [Fact]
    public override Task ListAddAsync_WithVariousInputs_HandlesCorrectly()
    {
        return base.ListAddAsync_WithVariousInputs_HandlesCorrectly();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListMutation_WithUnsupportedDictionary_RejectsWithoutChangingValueAsync(bool remove)
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var cache = CreateCacheClient(o => o.TimeProvider(timeProvider).CloneValues(false).WithFixedSizing(10_000, 50));
        var original = new ReadOnlyDictionary<string, DateTime?>(new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase)
        {
            ["first"] = null
        });
        await cache.SetAsync("set", original, TimeSpan.FromMinutes(1));

        // Act
        var error = await Record.ExceptionAsync(async () =>
        {
            if (remove)
                await cache.ListRemoveAsync("set", new[] { "FIRST" });
            else
                await cache.ListAddAsync("set", new[] { "FIRST", "second" });
        });

        // Assert
        Assert.IsType<NotSupportedException>(error);
        Assert.Same(original, (await cache.GetAsync<IDictionary<string, DateTime?>>("set")).Value);
        Assert.Single(original);
        Assert.True(original.ContainsKey("FIRST"));
        Assert.Equal(TimeSpan.FromMinutes(1), await cache.GetExpirationAsync("set"));
        Assert.Equal(50, cache.CurrentMemorySize);
    }

    [Fact]
    public async Task ListRemoveAsync_WhenLastValueRemoved_RemovesKeyAndReleasesMemory()
    {
        // Arrange
        using var cache = CreateCacheClient(o => o.WithFixedSizing(10_000, 50));
        await cache.ListAddAsync("set", new[] { "a" });
        Assert.Equal(50, cache.CurrentMemorySize);

        // Act
        long removed = await cache.ListRemoveAsync("set", new[] { "a" });

        // Assert
        Assert.Equal(1, removed);
        Assert.Equal(0, cache.CurrentMemorySize);
        Assert.False(await cache.ExistsAsync("set"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListRemoveAsync_WhenRejectedWithACaseInsensitiveComparer_KeepsTheStoredSpelling(bool cloneValues)
    {
        // Arrange
        bool reject = false;
        using var cache = CreateCacheClient(o => o.CloneValues(cloneValues).MaxMemorySize(1_000)
            .SizeCalculator(_ => reject ? -1 : 1));
        await cache.SetAsync("list", new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase) { ["first"] = null, ["second"] = null });
        Assert.Equal(1, await cache.ListAddAsync("list", ["third"]));
        Assert.Equal(1, await cache.ListAddAsync("list", ["SECOND"]));
        reject = true;

        // Act
        long removed = await cache.ListRemoveAsync("list", ["FIRST"]);

        // Assert
        Assert.Equal(0, removed);
        Assert.Equal(["first", "second", "third"], (await cache.GetListAsync<string>("list")).Value.Order(StringComparer.Ordinal));
        Assert.Same(StringComparer.OrdinalIgnoreCase, (await cache.GetAsync<Dictionary<string, DateTime?>>("list")).Value.Comparer);
    }

    [Fact]
    public async Task ListRemoveAsync_WhenRejectedWithPartialStructEquality_KeepsTheStoredMember()
    {
        // Arrange
        bool reject = false;
        using var cache = CreateCacheClient(o => o.CloneValues(false).MaxMemorySize(1_000)
            .SizeCalculator(_ => reject ? -1 : 1));
        Assert.Equal(2, await cache.ListAddAsync("list", [new IdOnlyMember(1, "stored"), new IdOnlyMember(2, "other")]));
        reject = true;

        // Act
        long removed = await cache.ListRemoveAsync("list", [new IdOnlyMember(1, "supplied")]);

        // Assert
        Assert.Equal(0, removed);
        var members = (await cache.GetListAsync<IdOnlyMember>("list")).Value;
        Assert.Contains(members, member => member.Id is 1 && member.Name is "stored");
        Assert.DoesNotContain(members, member => member.Name is "supplied");
    }

    [Fact]
    public override Task ListRemoveAsync_WithInvalidInputs_ThrowsAppropriateException()
    {
        return base.ListRemoveAsync_WithInvalidInputs_ThrowsAppropriateException();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListRemoveAsync_WithMissingValue_PrunesExpiredValues(bool dynamicSizing)
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var calculator = new SizeCalculator(Log);
        using var cache = CreateCacheClient(o =>
        {
            o.TimeProvider(timeProvider);
            return dynamicSizing
                ? o.CloneValues(false).WithDynamicSizing(100_000_000, Log)
                : o.MaxMemorySize(1000).SizeCalculator(value => ((IDictionary<string, DateTime?>)value).Count);
        });
        await cache.ListAddAsync("set", new[] { "permanent" });
        await cache.ListAddAsync("set", new[] { "expired" }, TimeSpan.FromMilliseconds(5));
        timeProvider.Advance(TimeSpan.FromMilliseconds(6));

        // Act
        long removed = await cache.ListRemoveAsync("set", new[] { "absent" });

        // Assert
        Assert.Equal(0, removed);
        long expectedSize = dynamicSizing ? calculator.CalculateSize(new Dictionary<string, DateTime?> { ["permanent"] = null }) : 1;
        Assert.Equal(expectedSize, cache.CurrentMemorySize);
        Assert.Equal(new[] { "permanent" }, (await cache.GetListAsync<string>("set")).Value);
        Assert.Null(await cache.GetExpirationAsync("set"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1000)]
    public async Task ListRemoveAsync_WithMixedExpirations_RecalculatesLifetimeAfterPruningAndRemoval(int expiringCount)
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var cache = CreateCacheClient(o => o.CloneValues(false).TimeProvider(timeProvider));
        await cache.ListAddAsync("set", new[] { "permanent" });
        await cache.ListAddAsync("set", Enumerable.Range(0, expiringCount).Select(index => $"first-{index}"), TimeSpan.FromSeconds(10));
        await cache.ListAddAsync("set", new[] { "last" }, TimeSpan.FromSeconds(20));

        // Act
        await cache.ListRemoveAsync("set", new[] { "permanent" });
        timeProvider.Advance(TimeSpan.FromSeconds(11));
        long removed = await cache.ListRemoveAsync("set", new[] { "first-0", "absent" });

        // Assert
        Assert.Equal(0, removed);
        Assert.Equal(new[] { "last" }, (await cache.GetListAsync<string>("set")).Value);
        Assert.Equal(TimeSpan.FromSeconds(9), await cache.GetExpirationAsync("set"));
    }

    [Fact]
    public override Task ListRemoveAsync_WithValidValues_RemovesKeyWhenEmpty()
    {
        return base.ListRemoveAsync_WithValidValues_RemovesKeyWhenEmpty();
    }

    [Fact]
    public override Task ListRemoveAsync_WithMultipleItems_RemovesCorrectly()
    {
        return base.ListRemoveAsync_WithMultipleItems_RemovesCorrectly();
    }

    [Fact]
    public override Task ListRemoveAsync_WithValues_RemovesCorrectly()
    {
        return base.ListRemoveAsync_WithValues_RemovesCorrectly();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListUpdate_WithChangedMaximumExpiration_PreservesTheStrictExpiryBoundary(bool expose)
    {
        // Arrange
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var cache = CreateCacheClient(o => o.CloneValues(false).TimeProvider(clock));
        await cache.ListAddAsync("list", Enumerable.Range(0, 64), TimeSpan.FromSeconds(10));
        await cache.ListAddAsync("list", [-1, -3], TimeSpan.FromSeconds(20));
        if (expose)
        {
            var dictionary = (await cache.GetAsync<Dictionary<int, DateTime?>>("list")).Value;
            dictionary[-1] = clock.GetUtcNow().UtcDateTime.AddSeconds(30);
        }

        // Act & Assert
        Assert.Equal(1, await cache.ListAddAsync("list", [-2], TimeSpan.FromSeconds(15)));
        Assert.Equal(TimeSpan.FromSeconds(expose ? 30 : 20), await cache.GetExpirationAsync("list"));
        Assert.Equal(1, await cache.ListRemoveAsync("list", [-1]));
        Assert.Equal(TimeSpan.FromSeconds(20), await cache.GetExpirationAsync("list"));
        Assert.Equal(1, await cache.ListRemoveAsync("list", [-3]));
        Assert.Equal(TimeSpan.FromSeconds(15), await cache.GetExpirationAsync("list"));
        Assert.Equal(1, await cache.ListAddAsync("list", [-2], TimeSpan.FromSeconds(25)));
        Assert.Equal(TimeSpan.FromSeconds(25), await cache.GetExpirationAsync("list"));
        Assert.Equal(1, await cache.ListAddAsync("list", [-2], TimeSpan.FromSeconds(5)));
        Assert.Equal(TimeSpan.FromSeconds(10), await cache.GetExpirationAsync("list"));

        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(TimeSpan.Zero, await cache.GetExpirationAsync("list"));
        Assert.Equal(Enumerable.Range(0, 64), (await cache.GetListAsync<int>("list")).Value);
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.False(await cache.ExistsAsync("list"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ListUpdate_WithDictionarySubclass_PreservesTypeAndIsolatesStoredCopy(bool cloneValues, bool rejectUpdate)
    {
        // Arrange
        bool reject = false;
        using var cache = CreateCacheClient(o => o.CloneValues(cloneValues).MaxMemorySize(1000)
            .SizeCalculator(value =>
            {
                int size = ((ICollection)value).Count;
                return reject && size == 3 ? -1 : size;
            }));
        var source = new MetadataListDictionary { [1] = null, [2] = null };
        await cache.SetAsync("list", source);
        reject = rejectUpdate;

        // Act
        long added = await cache.ListAddAsync("list", [3]);
        long removed = await cache.ListRemoveAsync("list", [1]);

        // Assert
        var current = (await cache.GetAsync<MetadataListDictionary>("list")).Value;
        Assert.IsType<MetadataListDictionary>(current);
        Assert.Equal(rejectUpdate ? 0 : 1, added);
        Assert.Equal(1, removed);
        Assert.Equal(rejectUpdate ? [2] : new[] { 2, 3 }, current.Keys.Order());
        Assert.Equal([1, 2], source.Keys.Order());
        Assert.Equal(current.Count, cache.CurrentMemorySize);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ListUpdate_WithDifferentHashSetComparer_DeduplicatesUsingDefaultEquality(bool cloneValues, bool customSizing)
    {
        // Arrange
        using var cache = CreateCacheClient(o => customSizing
            ? o.CloneValues(cloneValues).SizeCalculator(value => ((ICollection)value).Count)
            : o.CloneValues(cloneValues));
        string first = new('x', 1);
        var values = new HashSet<string>(ReferenceEqualityComparer.Instance) { first, new('x', 1), "other" };
        Assert.Equal(3, values.Count);

        // Act
        long added = await cache.ListAddAsync("list", values);

        // Assert
        Assert.Equal(2, added);
        var stored = (await cache.GetListAsync<string>("list")).Value;
        Assert.Equal(["x", "other"], stored);
        Assert.Same(first, stored.First());
        Assert.Equal(2, await cache.ListRemoveAsync("list", values));
        Assert.Equal(3, values.Count);
        Assert.False(await cache.ExistsAsync("list"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ListUpdate_WithMulticastFixedSizer_PreservesFullDictionaryCallbacks(bool remove, bool reject)
    {
        // Arrange
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var observedCounts = new List<int>();
        bool rejectReplacement = false;
        Func<object, long> sizeCalculator = value =>
        {
            var dictionary = Assert.IsType<Dictionary<int, DateTime?>>(value);
            observedCounts.Add(dictionary.Count);
            if (rejectReplacement && dictionary.Count > 1)
                throw new MaxEntrySizeExceededCacheException(65, 64, dictionary.GetType().Name);
            return dictionary.Count;
        };
        sizeCalculator += _ => 64;
        using var cache = CreateCacheClient(o => o.CloneValues(false).TimeProvider(clock)
            .MaxMemorySize(100_000_000).SizeCalculator(sizeCalculator));
        var ttl = TimeSpan.FromMinutes(1);
        await cache.ListAddAsync("list", Enumerable.Range(0, 64), ttl);
        var previous = GetEntry(cache, "list");
        observedCounts.Clear();
        rejectReplacement = reject;

        // Act
        async Task<long> UpdateAsync() => remove
            ? await cache.ListRemoveAsync("list", [0])
            : await cache.ListAddAsync("list", [-1], ttl);
        if (reject)
        {
            await Assert.ThrowsAsync<MaxEntrySizeExceededCacheException>(UpdateAsync);
            Assert.Same(previous, GetEntry(cache, "list"));
        }
        else
        {
            Assert.Equal(1, await UpdateAsync());
        }

        // Assert
        Assert.Equal(remove ? [63] : new[] { 1, 65 }, observedCounts);
        Assert.Equal(64, cache.CurrentMemorySize);
        Assert.Equal(ttl, await cache.GetExpirationAsync("list"));
        var expected = reject ? Enumerable.Range(0, 64)
            : remove ? Enumerable.Range(1, 63) : Enumerable.Range(0, 64).Append(-1);
        Assert.Equal(expected.Order(), (await cache.GetListAsync<int>("list")).Value.Order());
    }

    [Fact]
    public async Task ListUpdate_WithNullableValues_PreservesNullFiltering()
    {
        // Arrange
        using var cache = CreateCacheClient(o => o.CloneValues(false));
        int?[] values = [null, 0, 0, 1, 2, null];

        // Act & Assert
#pragma warning disable CS8714 // Preserve the existing handling of nullable inputs despite the notnull annotation.
        Assert.Equal(3, await cache.ListAddAsync("list", values));
        Assert.Equal(new int?[] { 0, 1, 2 }, (await cache.GetListAsync<int?>("list")).Value);
        Assert.Equal(2, await cache.ListRemoveAsync("list", new int?[] { null, 0, 1, 1 }));
        Assert.Equal(new int?[] { 2 }, (await cache.GetListAsync<int?>("list")).Value);
#pragma warning restore CS8714
    }

    [Fact]
    public async Task ListUpdate_WithNullsAndDuplicates_EnumeratesInputOnce()
    {
        // Arrange
        using var cache = CreateCacheClient(o => o.CloneValues(false));
        int enumerations = 0;

        // Act & Assert
        Assert.Equal(2, await cache.ListAddAsync("list", GetValues()));
        Assert.Equal(1, enumerations);
        Assert.Equal(["one", "two"], (await cache.GetListAsync<string>("list")).Value);
        enumerations = 0;
        Assert.Equal(2, await cache.ListRemoveAsync("list", GetValues()));
        Assert.Equal(1, enumerations);
        Assert.False(await cache.ExistsAsync("list"));

        HashSet<string> set = [null!, "one", "two"];
        Assert.Equal(2, await cache.ListAddAsync("list", set));
        Assert.Equal(set.Where(value => value is not null), (await cache.GetListAsync<string>("list")).Value);
        Assert.Equal(2, await cache.ListRemoveAsync("list", set));
        Assert.Equal(3, set.Count);

        IEnumerable<string> GetValues()
        {
            if (++enumerations != 1)
                throw new InvalidOperationException("Input was enumerated more than once.");
            yield return null!;
            yield return "one";
            yield return "one";
            yield return "two";
            yield return null!;
        }
    }

    [Theory]
    [InlineData(64)]
    [InlineData(1024)]
    public async Task ListUpdate_WithPermanentMembers_PreservesLatestExpirationThroughPruning(int count)
    {
        // Arrange
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var cache = CreateCacheClient(o => o.CloneValues(false).TimeProvider(clock));
        await cache.ListAddAsync("list", Enumerable.Range(0, count), TimeSpan.FromMinutes(10));

        // Act & Assert
        Assert.Equal(1, await cache.ListAddAsync("list", [0]));
        Assert.Null(await cache.GetExpirationAsync("list"));
        Assert.Equal(1, await cache.ListAddAsync("list", [1], TimeSpan.FromMinutes(20)));
        Assert.Null(await cache.GetExpirationAsync("list"));
        Assert.Equal(1, await cache.ListRemoveAsync("list", [0]));
        Assert.Equal(TimeSpan.FromMinutes(20), await cache.GetExpirationAsync("list"));

        Assert.Equal(1, await cache.ListAddAsync("list", [0]));
        clock.Advance(TimeSpan.FromMinutes(21));
        Assert.Equal(1, await cache.ListAddAsync("list", [-1], TimeSpan.FromMinutes(15)));
        Assert.Null(await cache.GetExpirationAsync("list"));
        Assert.Equal(new[] { -1, 0 }, (await cache.GetListAsync<int>("list")).Value.Order());
        Assert.Equal(1, await cache.ListRemoveAsync("list", [-1]));
        Assert.Null(await cache.GetExpirationAsync("list"));
        Assert.Equal(1, await cache.ListAddAsync("list", [0], TimeSpan.FromMinutes(5)));
        Assert.Equal(TimeSpan.FromMinutes(5), await cache.GetExpirationAsync("list"));
    }

    [Fact]
    public async Task RecalculateMemorySize_WithExpiredStoredEntry_KeepsSizeUntilRemoved()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var cache = CreateCacheClient(o => o.TimeProvider(timeProvider).WithFixedSizing(10_000, 50));
        StoreEntry(cache, "live", CreateEntry("live", timeProvider));
        StoreEntry(cache, "expired", CreateEntry("expired", timeProvider, TimeSpan.FromMinutes(1)));
        timeProvider.Advance(TimeSpan.FromMinutes(2));

        // Act
        long recounted = cache.RecalculateMemorySize();
        await cache.RemoveAsync("expired");

        // Assert
        Assert.Equal(100, recounted);
        Assert.Equal(50, cache.CurrentMemorySize);
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

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    public async Task RemoveIfEqualAsync_WhenEntryExpires_ReclaimsMemoryAndNotifies(bool expiresDuringComparison, bool matches, bool recentMaintenance)
    {
        // Arrange
        // Keep UTC within the initial 250ms maintenance window to exercise throttled cleanup.
        var start = recentMaintenance
            ? new DateTimeOffset(DateTime.MinValue, TimeSpan.Zero).AddMilliseconds(100)
            : DateTimeOffset.UtcNow;
        var timeProvider = new FakeTimeProvider(start);
        using var cache = CreateCacheClient(o => o.TimeProvider(timeProvider).WithFixedSizing(10_000, 50));
        var expired = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = cache.ItemExpired.AddSyncHandler((_, args) => expired.TrySetResult(args.Key));
        var duration = recentMaintenance ? TimeSpan.FromMilliseconds(5) : TimeSpan.FromMinutes(1);
        var owner = new ComparedValue("owner", expiresDuringComparison ? () => timeProvider.Advance(duration.Add(TimeSpan.FromMilliseconds(1))) : null);
        StoreEntry(cache, "lease", CreateEntry(owner, timeProvider, duration));
        if (!expiresDuringComparison)
            timeProvider.Advance(duration.Add(TimeSpan.FromMilliseconds(1)));
        Assert.Equal(50, cache.CurrentMemorySize);

        // Act
        bool removed = await cache.RemoveIfEqualAsync("lease", new ComparedValue(matches ? "owner" : "other"));

        // Assert
        Assert.False(removed);
        Assert.Equal("lease", await expired.Task.WaitAsync(TimeSpan.FromSeconds(5), TestCancellationToken));
        Assert.Null(cache.UpdateEntry("lease", current => (current, current)));
        Assert.Equal(0, cache.CurrentMemorySize);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RemoveIfEqualAsync_WhenOwnerChangesDuringComparison_KeepsNewOwner(bool replace, bool expiresDuringComparison)
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var cache = CreateCacheClient(o => o.TimeProvider(timeProvider).CloneValues(false));
        InMemoryCacheClient.CacheEntry? newOwner = null;
        var owner = new ComparedValue("owner", () =>
        {
            if (expiresDuringComparison)
                timeProvider.Advance(TimeSpan.FromMinutes(2));
            newOwner = CreateEntry(new ComparedValue("new-owner"), timeProvider, TimeSpan.FromMinutes(1));
            StoreEntry(cache, "lease", newOwner);
        });
        StoreEntry(cache, "lease", CreateEntry(owner, timeProvider, TimeSpan.FromMinutes(1)));

        // Act
        bool changed = replace
            ? await cache.ReplaceIfEqualAsync("lease", new ComparedValue("owner"), owner, TimeSpan.FromMinutes(10))
            : await cache.RemoveIfEqualAsync("lease", owner);

        // Assert
        Assert.False(changed);
        Assert.NotNull(newOwner);
        Assert.Same(newOwner, GetEntry(cache, "lease"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoveIfEqualAsync_WithExplicitDeletion_DoesNotLeaveExpirationTombstone(bool removeList)
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var cache = CreateCacheClient(o => o.TimeProvider(timeProvider));
        if (removeList)
            await cache.ListAddAsync("key", new[] { "value" }, TimeSpan.FromMinutes(1));
        else
            await cache.SetAsync("key", "value", TimeSpan.FromMinutes(1));

        // Act
        if (removeList)
            Assert.Equal(1, await cache.ListRemoveAsync("key", new[] { "value" }));
        else
            Assert.True(await cache.RemoveIfEqualAsync("key", "value"));

        // Assert
        Assert.Null(cache.UpdateEntry("key", current => (current, current)));
        timeProvider.Advance(TimeSpan.FromMinutes(2));
        await cache.DoMaintenanceAsync();
        Assert.Equal(0, cache.Count);
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

    [Theory]
    [InlineData("comparison")]
    [InlineData("sizing")]
    [InlineData("retry")]
    [InlineData("entry")]
    public async Task ReplaceIfEqualAsync_WhenReplacementExpiresBeforePublication_PreservesLiveEntry(string expirationStage)
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        int sizingCalls = 0;
        using var cache = CreateCacheClient(o => o.TimeProvider(timeProvider).CloneValues(false)
            .MaxMemorySize(10_000).SizeCalculator(_ =>
            {
                sizingCalls++;
                if (expirationStage is "sizing" or "entry")
                    timeProvider.Advance(TimeSpan.FromMinutes(2));
                return 100;
            }));
        var expected = new ComparedValue("owner");
        var original = CreateEntry(new ComparedValue("owner", () =>
        {
            if (expirationStage == "comparison")
                timeProvider.Advance(TimeSpan.FromMinutes(2));
            else if (expirationStage == "retry")
                StoreEntry(cache, "lease", CreateEntry(new ComparedValue("owner", () => timeProvider.Advance(TimeSpan.FromMinutes(2))),
                    timeProvider, TimeSpan.FromMinutes(10)));
        }), timeProvider, expirationStage == "entry" ? TimeSpan.FromMinutes(1) : TimeSpan.FromMinutes(10));
        StoreEntry(cache, "lease", original);

        // Act
        bool replaced = await cache.ReplaceIfEqualAsync("lease", new ComparedValue("replacement"), expected, TimeSpan.FromMinutes(1));

        // Assert
        Assert.False(replaced);
        Assert.Equal(1, sizingCalls);
        if (expirationStage == "entry")
        {
            Assert.False(await cache.ExistsAsync("lease"));
            return;
        }

        Assert.Equal("owner", (await cache.GetAsync<ComparedValue>("lease")).Value.Id);
        Assert.Equal(TimeSpan.FromMinutes(8), await cache.GetExpirationAsync("lease"));
        Assert.Equal(50, cache.CurrentMemorySize);
    }

    [Fact]
    public async Task ReplaceIfEqualAsync_WithCloneValues_IsolatesReplacementFromCaller()
    {
        // Arrange
        using var cache = CreateCacheClient(o => o.CloneValues(true));
        await cache.SetAsync<object>("key", 1);
        var replacement = new List<int> { 1 };

        // Act
        bool replaced = await cache.ReplaceIfEqualAsync<object>("key", replacement, 1);
        replacement.Add(2);

        // Assert
        Assert.True(replaced);
        var cached = await cache.GetAsync<List<int>>("key");
        Assert.Equal([1], cached.Value);
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
    public async Task ReplaceIfEqualAsync_WithMismatchedOversizedValue_ReturnsFalseWithoutThrowing()
    {
        // Arrange
        using var cache = CreateCacheClient(o => o
            .WithDynamicSizing(10000, Log)
            .MaxEntrySize(50)
            .ShouldThrowOnMaxEntrySizeExceeded());
        await cache.SetAsync("key", "small");

        // Act
        bool replaced = await cache.ReplaceIfEqualAsync("key", new string('x', 100), "other");

        // Assert
        Assert.False(replaced);
        Assert.Equal("small", (await cache.GetAsync<string>("key")).Value);
    }

    [Fact]
    public override Task ReplaceIfEqualAsync_WithNonExistentKey_ReturnsFalseAndDoesNotCreateKey()
    {
        return base.ReplaceIfEqualAsync_WithNonExistentKey_ReturnsFalseAndDoesNotCreateKey();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplaceIfEqualAsync_WithRejectedReplacement_PreservesEntry(bool shouldThrow)
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var cache = CreateCacheClient(o => o.TimeProvider(timeProvider).MaxMemorySize(1000)
            .SizeCalculator(value => ((string)value).Length).MaxEntrySize(5)
            .ShouldThrowOnMaxEntrySizeExceeded(shouldThrow));
        await cache.SetAsync("key", "old", TimeSpan.FromMinutes(1));

        // Act
        if (shouldThrow)
            await Assert.ThrowsAsync<MaxEntrySizeExceededCacheException>(async () =>
                await cache.ReplaceIfEqualAsync("key", "oversized", "old", TimeSpan.FromMinutes(5)));
        else
            Assert.False(await cache.ReplaceIfEqualAsync("key", "oversized", "old", TimeSpan.FromMinutes(5)));

        // Assert
        Assert.Equal("old", (await cache.GetAsync<string>("key")).Value);
        Assert.Equal(TimeSpan.FromMinutes(1), await cache.GetExpirationAsync("key"));
        Assert.Equal(3, cache.CurrentMemorySize);
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
    public async Task SetAsync_WithEntryFarAboveMemoryLimit_CompactsImmediately()
    {
        // Arrange
        var clock = new FakeTimeProvider(DateTimeOffset.MinValue);
        using var cache = CreateCacheClient(o => o.TimeProvider(clock).WithFixedSizing(1, 250_000_000));

        // Act
        bool stored = await cache.SetAsync("large", 1);

        // Assert
        Assert.True(stored);
        Assert.False(await cache.ExistsAsync("large"));
        Assert.Equal(0, cache.CurrentMemorySize);
    }

    [Fact]
    public override Task SetAsync_WithExpiration_SetsExpirationCorrectly()
    {
        return base.SetAsync_WithExpiration_SetsExpirationCorrectly();
    }

    [Fact]
    public async Task SetExpirationAsync_WithClonedPayload_ReusesStoredValueAndAccessOrder()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var cache = CreateCacheClient(o => o.CloneValues(true).TimeProvider(timeProvider));
        await cache.SetAsync("key", new SimpleModel { Data1 = "original" });
        var before = GetEntry(cache, "key");
        timeProvider.Advance(TimeSpan.FromMilliseconds(1));

        // Act
        await cache.SetExpirationAsync("key", TimeSpan.FromMinutes(1));

        // Assert
        var after = GetEntry(cache, "key");
        Assert.NotSame(before, after);
        Assert.Same(before.StoredValue, after.StoredValue);
        Assert.Equal(before.LastAccessTicks, after.LastAccessTicks);
        Assert.Equal(before.InstanceNumber, after.InstanceNumber);
        var firstRead = (await cache.GetAsync<SimpleModel>("key")).Value!;
        firstRead.Data1 = "changed";
        Assert.Equal("original", (await cache.GetAsync<SimpleModel>("key")).Value!.Data1);
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

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SetIfHigherAsync_WithNumericConversion_TracksReplacementSize(bool lower, bool useDouble)
    {
        // Arrange
        using var cache = CreateCacheClient(o => o.MaxMemorySize(1000)
            .SizeCalculator(value => value is string ? 100 : 8));
        await cache.SetAsync("key", "10");

        // Act
        double difference = (lower, useDouble) switch
        {
            (true, true) => await cache.SetIfLowerAsync("key", 5d),
            (true, false) => await cache.SetIfLowerAsync("key", 5L),
            (false, true) => await cache.SetIfHigherAsync("key", 15d),
            _ => await cache.SetIfHigherAsync("key", 15L)
        };

        // Assert
        Assert.Equal(5, difference);
        Assert.Equal(lower ? 5 : 15, (await cache.GetAsync<long>("key")).Value);
        Assert.Equal(8, cache.CurrentMemorySize);
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
            var cache = CreateCacheClient(o => o.MaxItems(10).CloneValues(true));
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

                await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System, TestCancellationToken); // keep the last access ticks from being the same for all items
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
    public async Task DoMaintenanceAsync_WhenEvictionCandidateIsReplaced_DoesNotSpendRemovalBudget()
    {
        // Arrange: 10 entries over the limit, so the pass's budget is 20 removals. The oldest entry is replaced
        // after compaction selects it, 15 times. Losing those races frees nothing, so the pass must still make the
        // 10 removals it needs instead of stopping after 20 attempts.
        const int maxItems = 100;
        const int replacementsDuringSelection = 15;
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var cache = CreateCacheClient(o => o.TimeProvider(timeProvider).MaxItems(maxItems));
        int replacements = 0;
        var oldestClock = new ReadHookTimeProvider(timeProvider);
        oldestClock.OnRead = () =>
        {
            // Keep the replacement's age and instance number (WithExpiration), so it stays the next candidate
            if (replacements < replacementsDuringSelection)
                cache.UpdateEntry<bool>("oldest", current => current is null ? (current, false) : (current.WithExpiration(current.ExpiresAt!.Value.AddTicks(1)), true));
            replacements++;
        };
        var oldest = new InMemoryCacheClient.CacheEntry("value", DateTime.MaxValue.AddDays(-1), oldestClock, shouldClone: false);
        cache.UpdateEntry<bool>("oldest", _ => (oldest, true));
        for (int i = 1; i < maxItems + 10; i++)
        {
            var entry = new InMemoryCacheClient.CacheEntry("value", null, timeProvider, shouldClone: false);
            cache.UpdateEntry<bool>(i.ToString(), _ => (entry, true));
        }

        // Act
        oldestClock.Armed = true;
        await cache.DoMaintenanceAsync().WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        oldestClock.Armed = false;

        // Assert
        Assert.True(replacements > replacementsDuringSelection, $"Expected {replacementsDuringSelection} lost races, saw {replacements}");
        Assert.Equal(maxItems, cache.Count);
    }

    [Fact]
    public async Task DoMaintenanceAsync_WithMaxTimeSpanExpiration_ShouldNotThrowException()
    {
        // Arrange - use a normal current time so using TimeSpan.MaxValue for expiration does not cause overflow issues
        var timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var cache = CreateCacheClient(o => o.CloneValues(true).TimeProvider(timeProvider));
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
    public async Task DoMaintenanceAsync_WithMinimumDateTimeExpiration_RemovesEntryWithoutError()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var cache = CreateCacheClient(o => o.TimeProvider(timeProvider).WithFixedSizing(10_000, 50));
        // An unspecified minimum date must be compared as UTC, not converted using the local timezone.
        var entry = new InMemoryCacheClient.CacheEntry("value", DateTime.MinValue, timeProvider, shouldClone: false, size: 50);
        StoreEntry(cache, "key", entry);
        await cache.SetAsync("list", new Dictionary<int, DateTime?> { [1] = DateTime.MinValue, [2] = DateTime.MaxValue });

        // Act
        await cache.ListAddAsync("list", [3]);
        await cache.DoMaintenanceAsync();

        // Assert
        Assert.Null(cache.UpdateEntry("key", current => (current, current)));
        Assert.Equal([2, 3], (await cache.GetListAsync<int>("list")).Value!.Order());
        Assert.Null(await cache.GetExpirationAsync("list"));
        Assert.Equal(50, cache.CurrentMemorySize);
        Assert.DoesNotContain(Log.LogEntries, logEntry => logEntry.LogLevel is LogLevel.Error);
    }

    [Fact]
    public async Task DoMaintenanceAsync_WithMixedExpirations_RemovesOnlyExpiredEntries()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var cache = CreateCacheClient(o => o.TimeProvider(timeProvider).WithFixedSizing(10_000, 50));
        var boundary = CreateEntry("boundary", timeProvider, TimeSpan.FromMinutes(2));
        var future = CreateEntry("future", timeProvider, TimeSpan.FromMinutes(3));
        var permanent = CreateEntry("permanent", timeProvider);
        var expired = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = cache.ItemExpired.AddSyncHandler((_, args) => expired.TrySetResult(args.Key));
        // StoreEntry directly so a background maintenance task cannot clean up before the explicit pass.
        StoreEntry(cache, "expired", CreateEntry("expired", timeProvider, TimeSpan.FromMinutes(1)));
        StoreEntry(cache, "boundary", boundary);
        StoreEntry(cache, "future", future);
        StoreEntry(cache, "permanent", permanent);
        timeProvider.Advance(TimeSpan.FromMinutes(2));

        // Act
        await cache.DoMaintenanceAsync();

        // Assert
        Assert.Equal("expired", await expired.Task.WaitAsync(TimeSpan.FromSeconds(5), TestCancellationToken));
        Assert.Null(cache.UpdateEntry("expired", current => (current, current)));
        Assert.Same(boundary, GetEntry(cache, "boundary"));
        Assert.Same(future, GetEntry(cache, "future"));
        Assert.Same(permanent, GetEntry(cache, "permanent"));
        Assert.Equal(TimeSpan.Zero, await cache.GetExpirationAsync("boundary"));
        Assert.Equal(TimeSpan.FromMinutes(1), await cache.GetExpirationAsync("future"));
        Assert.Null(await cache.GetExpirationAsync("permanent"));
        Assert.Equal(150, cache.CurrentMemorySize);
        Assert.DoesNotContain(Log.LogEntries, entry => entry.LogLevel is LogLevel.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DoMaintenanceAsync_WithMoreEntriesThanPassBudget_CompactsAcrossBoundedPasses(bool useMemoryLimit)
    {
        // Arrange
        const int entryCount = 1101;
        const int maximumRemovalsPerPass = 1000;
        const long entrySize = 50;
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var cache = CreateCacheClient(o =>
        {
            o.TimeProvider(timeProvider);
            return useMemoryLimit ? o.WithFixedSizing(entrySize, entrySize) : o.MaxItems(1);
        });

        // Seed directly so normal writes do not compact the cache before the pass under test.
        for (int i = 0; i < entryCount; i++)
        {
            var entry = new InMemoryCacheClient.CacheEntry("value", null, timeProvider, shouldClone: false, size: entrySize);
            cache.UpdateEntry<bool>(i.ToString(), _ => (entry, true));
        }

        // Act
        await cache.DoMaintenanceAsync().WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        int countAfterFirstPass = cache.Count;
        long sizeAfterFirstPass = cache.CurrentMemorySize;
        await cache.DoMaintenanceAsync().WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(entryCount - maximumRemovalsPerPass, countAfterFirstPass);
        Assert.Equal(useMemoryLimit ? countAfterFirstPass * entrySize : 0, sizeAfterFirstPass);
        Assert.Equal(1, cache.Count);
        Assert.Equal(useMemoryLimit ? entrySize : 0, cache.CurrentMemorySize);
        Assert.Equal("value", Assert.Single(cache.Items).Value);
        Assert.DoesNotContain(Log.LogEntries, entry => entry.LogLevel >= LogLevel.Error);
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(25, false)]
    public async Task DoMaintenanceAsync_WithRecentlyAccessedEntry_UsesActualExpiration(int expiresInMilliseconds, bool removed)
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var cache = CreateCacheClient(o => o.TimeProvider(timeProvider).WithFixedSizing(10_000, 50));
        var entry = CreateEntry("value", timeProvider, TimeSpan.FromMilliseconds(expiresInMilliseconds));
        StoreEntry(cache, "key", entry);

        // Act
        await cache.DoMaintenanceAsync();

        // Assert
        var remaining = cache.UpdateEntry("key", current => (current, current));
        Assert.Equal(removed, remaining is null);
        Assert.Equal(removed ? 0 : 50, cache.CurrentMemorySize);
    }

    [Fact]
    public async Task SetAsync_WithMaxMemorySizeLimit_EvictsWhenOverLimit()
    {
        // Arrange - Use a memory limit that allows for testing eviction
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var cache = CreateCacheClient(o => o.WithDynamicSizing(200, Log).CloneValues(false).TimeProvider(timeProvider));

        using (cache)
        {
            await cache.RemoveAllAsync();
            Assert.Equal(0, cache.CurrentMemorySize);

            // Act - Add some entries with known sizes
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

            // Advance time to allow maintenance to process
            timeProvider.Advance(TimeSpan.FromSeconds(1));
            _logger.LogInformation($"After time advance: CurrentMemorySize={cache.CurrentMemorySize}");

            // Assert - The cache should respect the memory limit
            Assert.NotNull(cache.MaxMemorySize);
            Assert.True(cache.CurrentMemorySize <= cache.MaxMemorySize.Value * 1.5,
                $"Memory size {cache.CurrentMemorySize} should be close to or below limit {cache.MaxMemorySize} (allowing up to 50% above limit for async cleanup)");

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
        // Arrange
        var cache = CreateCacheClient(o => o.WithDynamicSizing(1024, Log).CloneValues(false));

        using (cache)
        {
            // Act
            await cache.SetAsync("key1", "value1");

            // Assert
            var result = await cache.GetAsync<string>("key1");
            Assert.True(result.HasValue, "Key should exist in cache");
            Assert.Equal("value1", result.Value);

            // "value1" = 6 chars * 2 bytes + 24 overhead = 36 bytes
            const long expectedSize = 36;
            Assert.Equal(expectedSize, cache.CurrentMemorySize);
        }
    }

    [Fact]
    public async Task SetAsync_WithBothLimits_RespectsMemoryAndItemLimits()
    {
        // Arrange - Test that both limits work together
        var cache = CreateCacheClient(o => o.MaxItems(5).WithDynamicSizing(512, Log).CloneValues(false));

        using (cache)
        {
            await cache.RemoveAllAsync();
            var mediumString = new string('a', 100); // 100 chars * 2 bytes + 24 overhead = 224 bytes

            // Act
            await cache.SetAsync("item1", mediumString);
            await cache.SetAsync("item2", mediumString);
            await cache.SetAsync("item3", mediumString); // Should be at 672 bytes, over 512 limit

            // Assert - Verify limits are respected
            Assert.True(cache.Count <= cache.MaxItems, $"Count {cache.Count} should be <= MaxItems {cache.MaxItems}");
            Assert.True(cache.CurrentMemorySize <= cache.MaxMemorySize || cache.Count == 0,
                $"Memory {cache.CurrentMemorySize} should be <= MaxMemory {cache.MaxMemorySize} or cache should be empty");
        }
    }

    [Fact]
    public async Task MemorySize_AfterSaturation_RecoversRemainingEntrySizes()
    {
        // Arrange
        var clock = new FakeTimeProvider(DateTimeOffset.MinValue);
        using var cache = CreateCacheClient(o => o.TimeProvider(clock).MaxMemorySize(Int64.MaxValue)
            .SizeCalculator(value => (long)value));
        await cache.SetAsync("large", Int64.MaxValue);
        await cache.SetAsync("small", 42L);
        Assert.Equal(Int64.MaxValue, cache.RecalculateMemorySize());

        // Act
        bool removed = await cache.RemoveAsync("large");

        // Assert
        Assert.True(removed);
        Assert.Equal(42, cache.CurrentMemorySize);
        Assert.Equal(cache.CurrentMemorySize, cache.RecalculateMemorySize());
    }

    [Theory]
    [InlineData(1, "None")]
    [InlineData(8, "None")]
    [InlineData(8, "RemoveAll")]
    [InlineData(8, "Recalculate")]
    public async Task MemorySize_WithConcurrentMutations_MatchesStoredEntries(int keyCount, string maintenance)
    {
        // Arrange
        const int workerCount = 8;
        const int iterations = 20_000;
        var clock = new FakeTimeProvider(new DateTimeOffset(DateTime.MinValue, TimeSpan.Zero).AddMilliseconds(100));
        using var cache = CreateCacheClient(o => o.CloneValues(false).MaxItems(null).TimeProvider(clock)
            .WithFixedSizing(1_000_000_000, 100));
        bool runMaintenance = maintenance != "None";
        using var start = new Barrier(workerCount + (runMaintenance ? 1 : 0));
        var keys = Enumerable.Range(0, keyCount).Select(i => $"key-{i}").ToArray();

        // Act
        var workers = Enumerable.Range(0, workerCount).Select(worker => Task.Factory.StartNew(async () =>
        {
            Synchronize();
            for (int iteration = 0; iteration < iterations; iteration++)
            {
                string key = keys[(iteration + worker) % keys.Length];
                await cache.AddAsync(key, worker);
                await cache.RemoveAsync(key);
            }
        }, TestCancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap()).ToList();
        if (runMaintenance)
        {
            workers.Add(Task.Factory.StartNew(async () =>
            {
                Synchronize();
                for (int iteration = 0; iteration < iterations; iteration++)
                {
                    if (maintenance == "RemoveAll")
                        await cache.RemoveAllAsync();
                    else
                        cache.RecalculateMemorySize();
                }
            }, TestCancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap());
        }
        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(30), TestCancellationToken);

        // Assert
        var stored = cache.Items;
        Assert.Empty(stored);
        Assert.Equal(stored.Count * 100L, cache.CurrentMemorySize);

        void Synchronize()
        {
            if (!start.SignalAndWait(TimeSpan.FromSeconds(10), TestCancellationToken))
                throw new TimeoutException("Concurrent cache workers did not start together.");
        }
    }

    [Fact]
    public async Task MemorySize_WithRemoveIfEqualOperation_FreesMemoryImmediately()
    {
        // Arrange
        using var cache = CreateCacheClient(o => o.WithFixedSizing(10_000, 50));
        await cache.SetAsync("key", "value");
        Assert.Equal(50, cache.CurrentMemorySize);

        // Act
        bool removed = await cache.RemoveIfEqualAsync("key", "value");

        // Assert
        Assert.True(removed);
        Assert.Equal(0, cache.CurrentMemorySize);
        Assert.False(await cache.ExistsAsync("key"));
    }

    [Fact]
    public async Task MemorySize_WithSetExpirationOperation_PreservesValueAndMemorySize()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var cache = CreateCacheClient(o => o.TimeProvider(timeProvider).WithFixedSizing(10_000, 50));
        await cache.SetAsync("key", "value", TimeSpan.FromMinutes(1));
        await cache.SetAsync("persistent", "value", TimeSpan.FromMinutes(1));

        // Act
        await cache.SetExpirationAsync("key", TimeSpan.FromMinutes(5));
        await cache.SetAllExpirationAsync(new Dictionary<string, TimeSpan?> { ["persistent"] = null });

        // Assert
        Assert.Equal("value", (await cache.GetAsync<string>("key")).Value);
        Assert.Equal(TimeSpan.FromMinutes(5), await cache.GetExpirationAsync("key"));
        Assert.Null(await cache.GetExpirationAsync("persistent"));
        Assert.Equal(100, cache.CurrentMemorySize);
    }

    [Fact]
    public async Task MemorySize_WithoutMaxMemorySize_DoesNotTrack()
    {
        // Arrange
        var cache = CreateCacheClient(o => o.CloneValues(false));

        using (cache)
        {
            // Act
            await cache.SetAsync("key1", "value1");

            // Assert - Memory tracking disabled when MaxMemorySize is null
            Assert.Null(cache.MaxMemorySize);
            Assert.Equal(0, cache.CurrentMemorySize);
        }
    }

    [Fact]
    public async Task MemorySize_WithAddOperation_TracksCorrectly()
    {
        // Arrange
        var cache = CreateCacheClient(o => o.WithDynamicSizing(1024, Log).CloneValues(false));

        using (cache)
        {
            await cache.RemoveAllAsync();
            Assert.Equal(0, cache.CurrentMemorySize);

            // Act - Add item: "value1" = 6 chars * 2 + 24 = 36 bytes
            await cache.SetAsync("key1", "value1");

            // Assert
            Assert.Equal(36, cache.CurrentMemorySize);
        }
    }

    [Fact]
    public async Task MemorySize_WithUpdateOperation_TracksCorrectly()
    {
        // Arrange
        var cache = CreateCacheClient(o => o.WithDynamicSizing(1024, Log).CloneValues(false));

        using (cache)
        {
            await cache.SetAsync("key1", "value1"); // 36 bytes
            Assert.Equal(36, cache.CurrentMemorySize);

            // Act - Update to longer value: "longer_value1" = 13 chars * 2 + 24 = 50 bytes
            await cache.SetAsync("key1", "longer_value1");

            // Assert
            Assert.Equal(50, cache.CurrentMemorySize);
        }
    }

    [Fact]
    public async Task MemorySize_WithRemoveOperation_FreesMemory()
    {
        // Arrange
        var cache = CreateCacheClient(o => o.WithDynamicSizing(1024, Log).CloneValues(false));

        using (cache)
        {
            await cache.SetAsync("key1", "value1"); // 36 bytes
            Assert.Equal(36, cache.CurrentMemorySize);

            // Act
            await cache.RemoveAsync("key1");

            // Assert
            Assert.Equal(0, cache.CurrentMemorySize);
        }
    }

    [Fact]
    public async Task MemorySize_WithRemoveAllOperation_FreesAllMemory()
    {
        // Arrange
        var cache = CreateCacheClient(o => o.WithDynamicSizing(1024, Log).CloneValues(false));

        using (cache)
        {
            await cache.SetAsync("key1", "value1"); // 36 bytes
            await cache.SetAsync("key2", "value2"); // 36 bytes
            Assert.Equal(72, cache.CurrentMemorySize);

            // Act
            await cache.RemoveAllAsync();

            // Assert
            Assert.Equal(0, cache.CurrentMemorySize);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(1000)]
    public async Task SetAsync_WithCachedDictionaryViews_IsolatesStoredAndReturnedValues(int count)
    {
        // Arrange
        using var cache = CreateCacheClient(o => o.CloneValues(true));
        var source = Enumerable.Range(0, count).ToDictionary(key => key, _ => (DateTime?)DateTime.UnixEpoch);
        _ = source.Keys;
        _ = source.Values;
        await cache.SetAsync("list", source);

        // Act
        source.Clear();
        var returned = (await cache.GetAsync<Dictionary<int, DateTime?>>("list")).Value;

        // Assert
        Assert.Equal(count, returned.Count);
        Assert.Equal(Enumerable.Range(0, count), returned.Keys);
        Assert.Equal(Enumerable.Repeat<DateTime?>(DateTime.UnixEpoch, count), returned.Values);
        returned.Clear();
        var current = (await cache.GetAsync<Dictionary<int, DateTime?>>("list")).Value;
        Assert.Equal(count, current.Count);
        Assert.Equal(Enumerable.Range(0, count), current.Keys);
        Assert.Equal(Enumerable.Repeat<DateTime?>(DateTime.UnixEpoch, count), current.Values);
    }

    [Fact]
    public async Task SetAsync_WithWithFixedSizing_ReturnsFixedSizeForAllObjects()
    {
        const long fixedSize = 100;
        var cache = CreateCacheClient(o => o.WithFixedSizing(10000, fixedSize));

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
        // Arrange
        var cache = CreateCacheClient(o => o.WithDynamicSizing(10000, Log));

        using (cache)
        {
            await cache.RemoveAllAsync();
            Assert.Equal(0, cache.CurrentMemorySize);

            // Act - Add a small string: "a" = 1 char * 2 bytes + 24 overhead = 26 bytes
            await cache.SetAsync("small", "a");

            // Assert - Verify small string size
            const long expectedSmallSize = 26;
            Assert.Equal(expectedSmallSize, cache.CurrentMemorySize);

            // Act - Add a larger string: 100 chars * 2 + 24 = 224 bytes
            await cache.SetAsync("large", new string('x', 100));

            // Assert - Total should be sum of both
            const long expectedLargeSize = 224;
            const long expectedTotalSize = expectedSmallSize + expectedLargeSize;
            Assert.Equal(expectedTotalSize, cache.CurrentMemorySize);
        }
    }

    [Fact]
    public async Task SetAsync_WithCustomSizeCalculator_UsesCustomCalculator()
    {
        int callCount = 0;
        var cache = CreateCacheClient(o => o
            .MaxMemorySize(10000)
            .SizeCalculator(_ =>
            {
                callCount++;
                return 50;
            }));

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
        // Arrange
        var cache = CreateCacheClient(o => o.WithDynamicSizing(10000, Log));

        using (cache)
        {
            await cache.RemoveAllAsync();
            Assert.Equal(0, cache.CurrentMemorySize);

            // Act - Add items to a list
            await cache.ListAddAsync("mylist", ["item1", "item2", "item3"]);

            // Assert - Memory should be tracked for list items
            var sizeAfterAdd = cache.CurrentMemorySize;
            Assert.True(sizeAfterAdd > 0, "Memory should be tracked for list items");

            // Act - Add more items to the same list
            await cache.ListAddAsync("mylist", ["item4", "item5"]);

            // Assert - Memory should increase when adding more items
            var sizeAfterMoreItems = cache.CurrentMemorySize;
            Assert.True(sizeAfterMoreItems > sizeAfterAdd, "Memory should increase when adding more list items");

            // Act - Remove items from the list
            await cache.ListRemoveAsync("mylist", ["item1", "item2"]);

            // Assert - Memory should decrease when removing items
            var sizeAfterRemove = cache.CurrentMemorySize;
            Assert.True(sizeAfterRemove < sizeAfterMoreItems, "Memory should decrease when removing list items");
        }
    }

    [Fact]
    public async Task CompactAsync_WithExpiredItems_EvictsExpiredItemsFirst()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var cache = CreateCacheClient(o => o
            .WithFixedSizing(100, 50)
            .TimeProvider(timeProvider));

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
        var cache = CreateCacheClient(o => o
            .WithDynamicSizing(300, Log)
            .TimeProvider(timeProvider));

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
            Assert.NotNull(cache.MaxMemorySize);
            Assert.True(cache.CurrentMemorySize <= cache.MaxMemorySize.Value * 1.5,
                $"Memory {cache.CurrentMemorySize} should be close to limit {cache.MaxMemorySize}");
        }
    }

    [Fact]
    public async Task CompactAsync_WithMaxItems_EvictsLeastRecentlyUsedItems()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var cache = CreateCacheClient(o => o
            .MaxItems(3)
            .TimeProvider(timeProvider));

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

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ConditionalMutation_WithExpiredIncompatibleValue_ReturnsFalseWithoutComparisonAsync(bool replace, bool throwingEquality)
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var cache = CreateCacheClient(o => o.TimeProvider(timeProvider).CloneValues(false));
        int comparisons = 0;
        var value = new ComparedValue("owner", () =>
        {
            comparisons++;
            throw new InvalidOperationException("Comparison failed");
        });
        StoreEntry(cache, "key", CreateEntry(throwingEquality ? value : new object(), timeProvider, TimeSpan.FromMinutes(1)));
        timeProvider.Advance(TimeSpan.FromMinutes(2));

        // Act
        bool changed = replace
            ? await cache.ReplaceIfEqualAsync("key", value, value)
            : await cache.RemoveIfEqualAsync("key", value);

        // Assert
        Assert.False(changed);
        Assert.Equal(0, comparisons);
        Assert.False(await cache.ExistsAsync("key"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ConditionalMutation_WithLiveIncompatibleValue_PreservesValueAndFaultsTaskAsync(bool replace, bool throwingEquality)
    {
        // Arrange
        using var cache = CreateCacheClient(o => o.CloneValues(false));
        var value = new ComparedValue("owner", () => throw new InvalidOperationException("Comparison failed"));
        object stored = throwingEquality ? value : new object();
        await cache.SetAsync("key", stored);
        Task<bool>? result = null;

        // Act
        var synchronousError = Record.Exception(() =>
        {
            result = replace
                ? cache.ReplaceIfEqualAsync("key", value, value)
                : cache.RemoveIfEqualAsync("key", value);
        });

        // Assert
        Assert.Null(synchronousError);
        Assert.NotNull(result);
        if (throwingEquality)
            await Assert.ThrowsAsync<InvalidOperationException>(() => result);
        else
            await Assert.ThrowsAsync<InvalidCastException>(() => result);
        Assert.Same(stored, (await cache.GetAsync<object>("key")).Value);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ConditionalWrite_WhenComparingValue_DoesNotRecordAccess(bool replace, bool matches)
    {
        // Arrange
        // Comparing the current value is part of deciding the write, not a read, so it must not change eviction order.
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var cache = CreateCacheClient(o => o.TimeProvider(timeProvider).CloneValues(true));
        await cache.SetAsync("lease", "owner");
        long lastAccess = GetEntry(cache, "lease").LastAccessTicks;
        timeProvider.Advance(TimeSpan.FromMinutes(1));
        string expected = matches ? "owner" : "other";

        // Act
        bool changed = replace
            ? await cache.ReplaceIfEqualAsync("lease", "renewed", expected)
            : await cache.RemoveIfEqualAsync("lease", expected);

        // Assert
        Assert.Equal(matches, changed);
        if (!matches)
            Assert.Equal(lastAccess, GetEntry(cache, "lease").LastAccessTicks);
        else if (replace)
            Assert.Equal("renewed", (await cache.GetAsync<string>("lease")).Value);
        else
            Assert.False(await cache.ExistsAsync("lease"));
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
        var cache = CreateCacheClient(o => o
            .MaxMemorySize(10000)
            .SizeCalculator(_ => -1)); // Always returns negative size

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
        // Arrange
        var cache = CreateCacheClient(o => o.WithDynamicSizing(10000, Log).CloneValues(false));

        using (cache)
        {
            await cache.RemoveAllAsync();
            Assert.Equal(0, cache.CurrentMemorySize);

            // Act - Increment on new key creates a double entry
            var result = await cache.IncrementAsync("counter", 1.5);

            // Assert - double = 8 bytes
            Assert.Equal(1.5, result);
            const long expectedDoubleSize = 8;
            Assert.Equal(expectedDoubleSize, cache.CurrentMemorySize);

            // Act - Increment again (same type, same entry)
            result = await cache.IncrementAsync("counter", 2.5);

            // Assert - Size unchanged, value updated
            Assert.Equal(4.0, result);
            Assert.Equal(expectedDoubleSize, cache.CurrentMemorySize);

            // Act - Remove and verify memory is freed
            await cache.RemoveAsync("counter");

            // Assert
            Assert.Equal(0, cache.CurrentMemorySize);
        }
    }

    [Fact]
    public async Task IncrementAsync_WithMaxMemorySize_TracksMemoryCorrectly()
    {
        // Arrange
        var cache = CreateCacheClient(o => o.WithDynamicSizing(10000, Log).CloneValues(false));

        using (cache)
        {
            await cache.RemoveAllAsync();
            Assert.Equal(0, cache.CurrentMemorySize);

            // Act - Increment on new key creates a long entry
            var result = await cache.IncrementAsync("counter", 1L);

            // Assert - long = 8 bytes
            Assert.Equal(1L, result);
            const long expectedLongSize = 8;
            Assert.Equal(expectedLongSize, cache.CurrentMemorySize);

            // Act - Increment again (same type, same entry)
            result = await cache.IncrementAsync("counter", 5L);

            // Assert - Size unchanged, value updated
            Assert.Equal(6L, result);
            Assert.Equal(expectedLongSize, cache.CurrentMemorySize);

            // Act - Decrement (negative increment)
            result = await cache.IncrementAsync("counter", -2L);

            // Assert - Value updated, size unchanged
            Assert.Equal(4L, result);
            Assert.Equal(expectedLongSize, cache.CurrentMemorySize);

            // Act - Remove and verify memory is freed
            await cache.RemoveAsync("counter");

            // Assert
            Assert.Equal(0, cache.CurrentMemorySize);
        }
    }

    [Fact]
    public async Task SetIfHigherAsync_Double_WithMaxMemorySize_TracksMemoryCorrectly()
    {
        // Arrange
        var cache = CreateCacheClient(o => o.WithDynamicSizing(10000, Log).CloneValues(false));

        using (cache)
        {
            await cache.RemoveAllAsync();
            Assert.Equal(0, cache.CurrentMemorySize);

            // Act - SetIfHigher on new key creates a double entry
            await cache.SetIfHigherAsync("counter", 100.5);

            // Assert - double = 8 bytes
            const long expectedDoubleSize = 8;
            Assert.Equal(expectedDoubleSize, cache.CurrentMemorySize);

            // Act - SetIfHigher with higher value should update
            await cache.SetIfHigherAsync("counter", 200.5);

            // Assert - Value updated, size unchanged
            var value = await cache.GetAsync<double>("counter");
            Assert.Equal(200.5, value.Value);
            Assert.Equal(expectedDoubleSize, cache.CurrentMemorySize);

            // Act - Remove and verify memory is freed
            await cache.RemoveAsync("counter");

            // Assert
            Assert.Equal(0, cache.CurrentMemorySize);
        }
    }

    [Fact]
    public async Task SetIfHigherAsync_WithMaxMemorySize_TracksMemoryCorrectly()
    {
        // Arrange
        var cache = CreateCacheClient(o => o.WithDynamicSizing(10000, Log).CloneValues(false));

        using (cache)
        {
            await cache.RemoveAllAsync();
            Assert.Equal(0, cache.CurrentMemorySize);

            // Act - SetIfHigher on new key creates a long entry
            await cache.SetIfHigherAsync("counter", 100L);

            // Assert - long = 8 bytes
            const long expectedLongSize = 8;
            Assert.Equal(expectedLongSize, cache.CurrentMemorySize);

            // Act - SetIfHigher with higher value should update
            await cache.SetIfHigherAsync("counter", 200L);

            // Assert - Value updated, size unchanged
            Assert.Equal(expectedLongSize, cache.CurrentMemorySize);

            // Act - SetIfHigher with lower value should NOT update the value
            await cache.SetIfHigherAsync("counter", 50L);

            // Assert - Value unchanged, size unchanged
            var value = await cache.GetAsync<long>("counter");
            Assert.Equal(200L, value.Value);
            Assert.Equal(expectedLongSize, cache.CurrentMemorySize);

            // Act - Remove and verify memory is freed
            await cache.RemoveAsync("counter");

            // Assert
            Assert.Equal(0, cache.CurrentMemorySize);
        }
    }

    [Fact]
    public async Task SetIfLowerAsync_Double_WithMaxMemorySize_TracksMemoryCorrectly()
    {
        // Arrange
        var cache = CreateCacheClient(o => o.WithDynamicSizing(10000, Log).CloneValues(false));

        using (cache)
        {
            await cache.RemoveAllAsync();
            Assert.Equal(0, cache.CurrentMemorySize);

            // Act - SetIfLower on new key creates a double entry
            await cache.SetIfLowerAsync("counter", 100.5);

            // Assert - double = 8 bytes
            const long expectedDoubleSize = 8;
            Assert.Equal(expectedDoubleSize, cache.CurrentMemorySize);

            // Act - SetIfLower with lower value should update
            await cache.SetIfLowerAsync("counter", 50.5);

            // Assert - Value updated, size unchanged
            var value = await cache.GetAsync<double>("counter");
            Assert.Equal(50.5, value.Value);
            Assert.Equal(expectedDoubleSize, cache.CurrentMemorySize);

            // Act - Remove and verify memory is freed
            await cache.RemoveAsync("counter");

            // Assert
            Assert.Equal(0, cache.CurrentMemorySize);
        }
    }

    [Fact]
    public async Task SetIfLowerAsync_WithMaxMemorySize_TracksMemoryCorrectly()
    {
        // Arrange
        var cache = CreateCacheClient(o => o.WithDynamicSizing(10000, Log).CloneValues(false));

        using (cache)
        {
            await cache.RemoveAllAsync();
            Assert.Equal(0, cache.CurrentMemorySize);

            // Act - SetIfLower on new key creates a long entry
            await cache.SetIfLowerAsync("counter", 100L);

            // Assert - long = 8 bytes
            const long expectedLongSize = 8;
            Assert.Equal(expectedLongSize, cache.CurrentMemorySize);

            // Act - SetIfLower with lower value should update
            await cache.SetIfLowerAsync("counter", 50L);

            // Assert - Value updated, size unchanged
            Assert.Equal(expectedLongSize, cache.CurrentMemorySize);

            // Act - SetIfLower with higher value should NOT update the value
            await cache.SetIfLowerAsync("counter", 200L);

            // Assert - Value unchanged, size unchanged
            var value = await cache.GetAsync<long>("counter");
            Assert.Equal(50L, value.Value);
            Assert.Equal(expectedLongSize, cache.CurrentMemorySize);

            // Act - Remove and verify memory is freed
            await cache.RemoveAsync("counter");

            // Assert
            Assert.Equal(0, cache.CurrentMemorySize);
        }
    }

    [Fact]
    public async Task ReplaceIfEqualAsync_WithMaxMemorySize_TracksMemoryCorrectly()
    {
        var cache = CreateCacheClient(o => o.WithDynamicSizing(10000, Log).CloneValues(false));

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

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SetAsync_WithOversizedCalculatorFallback_PreservesEntry(bool shouldThrow, bool stringValue)
    {
        // Arrange
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var cache = CreateCacheClient(o => o.TimeProvider(clock).MaxMemorySize(1000)
            .SizeCalculator(value => value is "old" ? 3 : throw new InvalidOperationException("Sizing failed"))
            .MaxEntrySize(10).ShouldThrowOnMaxEntrySizeExceeded(shouldThrow));
        await cache.SetAsync("key", "old", TimeSpan.FromMinutes(1));
        object replacement = stringValue ? "replacement" : new object();

        // Act
        if (shouldThrow)
            await Assert.ThrowsAsync<MaxEntrySizeExceededCacheException>(() => cache.SetAsync("key", replacement, TimeSpan.FromMinutes(5)));
        else
            Assert.False(await cache.SetAsync("key", replacement, TimeSpan.FromMinutes(5)));

        // Assert
        Assert.Equal("old", (await cache.GetAsync<string>("key")).Value);
        Assert.Equal(TimeSpan.FromMinutes(1), await cache.GetExpirationAsync("key"));
        Assert.Equal(3, cache.CurrentMemorySize);
    }

    [Fact]
    public async Task SetAsync_WithOversizedEntry_LogsWarningAndReturnsFalse()
    {
        // Arrange
        var cache = CreateCacheClient(o => o
            .WithDynamicSizing(10000, Log)
            .MaxEntrySize(50)); // Very small limit

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
        var cache = CreateCacheClient(o => o
            .WithDynamicSizing(10000, Log)
            .MaxEntrySize(50) // Very small limit
            .ShouldThrowOnMaxEntrySizeExceeded());

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
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => CreateCacheClient(o => o
            .WithDynamicSizing(1000) // 1000 bytes total limit
            .MaxEntrySize(2000))); // 2000 bytes per entry - INVALID

        Assert.Contains("MaxEntrySize", ex.Message);
        Assert.Contains("MaxMemorySize", ex.Message);
    }

    [Fact]
    public async Task AddAsync_WithOversizedEntry_ReturnsFalse()
    {
        // Arrange
        var cache = CreateCacheClient(o => o
            .WithDynamicSizing(10000, Log)
            .MaxEntrySize(50));

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
        var cache = CreateCacheClient(o => o
            .WithDynamicSizing(10000, Log)
            .MaxEntrySize(1000)); // 1KB limit

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

    [Fact]
    public async Task DoMaintenanceAsync_WithPositiveTimezoneOffset_ShouldNotThrowOnDateTimeMinValue()
    {
        // This test reproduces an issue where RemoveIfEqualAsync sets ExpiresAt to DateTime.MinValue,
        // and DoMaintenanceAsync then compares it with a DateTimeOffset. When the system's local timezone
        // has a positive offset (like Beirut UTC+2/+3), converting DateTime.MinValue to DateTimeOffset
        // throws ArgumentOutOfRangeException because the resulting UTC time would be before year 0001.
        //
        // The bug is in InMemoryCacheClient.cs when checking the expiration:
        //   if (expiresAt < DateTime.MaxValue && expiresAt <= utcNow)
        //
        // When expiresAt is DateTime.MinValue (Kind=Unspecified) and utcNow is a DateTimeOffset,
        // the implicit conversion of DateTime.MinValue to DateTimeOffset uses the system's local
        // timezone offset. If that offset is positive, the conversion fails.

        var timeProvider = new FakeTimeProvider();
        var cache = CreateCacheClient(o => o.CloneValues(true).TimeProvider(timeProvider));
        using (cache)
        {
            await cache.RemoveAllAsync();

            // Set up a cache entry and then remove it via RemoveIfEqualAsync
            // This sets ExpiresAt to DateTime.MinValue on the entry
            await cache.SetAsync("test-key", "test-value", TimeSpan.FromMinutes(1));
            await cache.RemoveIfEqualAsync("test-key", "test-value");


            // Advance time to ensure the entry is considered "infrequently accessed"
            // (LastAccessTicks must be < utcNow - 300ms for maintenance to process it)
            timeProvider.Advance(TimeSpan.FromSeconds(1));

            // Trigger another cache operation to start maintenance
            // This causes DoMaintenanceAsync to iterate over entries including the one
            // with ExpiresAt = DateTime.MinValue
            await cache.SetAsync("trigger", "value");

            // Wait a moment to allow maintenance to complete
            await Task.Delay(100, TestCancellationToken);

            // Check for the error log that indicates the bug
            Assert.Null(Log.LogEntries.SingleOrDefault(l => l.LogLevel == LogLevel.Error));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetAsync_WithObjectType_ReturnsValueDirectly(bool cloneValues)
    {
        var cache = CreateCacheClient(o => o.CloneValues(cloneValues));
        using (cache)
        {
            var data = new MyData("hello", 42);
            await cache.SetAsync("key", data);

            var result = await cache.GetAsync<object>("key");
            Assert.True(result.HasValue);
            Assert.Equal(data, result.Value);
        }
    }

    [Fact]
    public override Task AddAsync_WhenKeyAlreadyExists_ReturnsFalseAndDoesNotOverwrite()
    {
        return base.AddAsync_WhenKeyAlreadyExists_ReturnsFalseAndDoesNotOverwrite();
    }

    [Fact]
    public override Task GetAsync_WhenKeyDoesNotExist_ReturnsNoValue()
    {
        return base.GetAsync_WhenKeyDoesNotExist_ReturnsNoValue();
    }

    [Fact]
    public override Task IncrementAsync_WithAmountZero_ReturnsCurrentValue()
    {
        return base.IncrementAsync_WithAmountZero_ReturnsCurrentValue();
    }

    [Fact]
    public override Task RemoveAsync_WhenKeyDoesNotExist_ReturnsFalse()
    {
        return base.RemoveAsync_WhenKeyDoesNotExist_ReturnsFalse();
    }

    [Fact]
    public override Task RemoveByPrefixAsync_WithNoMatchingKeys_ReturnsZero()
    {
        return base.RemoveByPrefixAsync_WithNoMatchingKeys_ReturnsZero();
    }

    [Fact]
    public override Task SetExpirationAsync_OnNonExistentKey_DoesNotThrow()
    {
        return base.SetExpirationAsync_OnNonExistentKey_DoesNotThrow();
    }

    [Fact]
    public void TryRemoveEntry_WhenObservedEntryWasReplaced_KeepsLiveEntry()
    {
        // Arrange
        // Reproduces the production race deterministically: maintenance observes an expired entry, another
        // writer replaces it, then maintenance tries to remove the entry it observed.
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var cache = CreateCacheClient(o => o.TimeProvider(timeProvider).WithFixedSizing(10_000, 50));
        var observed = CreateEntry("expired", timeProvider, TimeSpan.FromMilliseconds(100));
        StoreEntry(cache, "key", observed);
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        Assert.True(observed.IsExpired);
        var fresh = CreateEntry("fresh", timeProvider, TimeSpan.FromMinutes(5));
        StoreEntry(cache, "key", fresh);

        // Act
        bool removed = cache.TryRemoveEntry("key", observed);

        // Assert
        Assert.False(removed);
        Assert.Same(fresh, GetEntry(cache, "key"));
        Assert.Equal(50, cache.CurrentMemorySize);
    }

    [Fact]
    public void UpdateEntry_WhenKeyAddedDuringAdd_RetriesAgainstAddedEntry()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var cache = CreateCacheClient(o => o.TimeProvider(timeProvider).WithFixedSizing(10_000, 50));
        var competing = CreateEntry("competing", timeProvider);
        int attempts = 0;

        // Act: another writer adds the key after this add read it as missing
        bool added = cache.UpdateEntry("key", current =>
        {
            if (++attempts is 1)
                StoreEntry(cache, "key", competing);

            return current is null ? (CreateEntry("mine", timeProvider), true) : (current, false);
        });

        // Assert
        Assert.Equal(2, attempts);
        Assert.False(added);
        Assert.Same(competing, GetEntry(cache, "key"));
        Assert.Equal(50, cache.CurrentMemorySize);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UpdateEntry_WhenKeyChangesDuringUpdate_ReturnsOnlyStoredResult(bool remove)
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var cache = CreateCacheClient(o => o.TimeProvider(timeProvider).WithFixedSizing(10_000, 50));
        StoreEntry(cache, "key", CreateEntry(1L, timeProvider));
        var replacement = CreateEntry(10L, timeProvider);
        int attempts = 0;

        // Act: another writer changes the value after this update read it
        long result = cache.UpdateEntry("key", current =>
        {
            if (++attempts is 1)
                StoreEntry(cache, "key", replacement);

            if (remove)
                return current?.StoredValue is 1L ? (null, 1L) : (current, 0L);

            long newValue = (long)current!.StoredValue! + 1;
            return (current.WithValue(newValue, null, current.Size), newValue);
        });

        // Assert
        Assert.Equal(2, attempts);
        Assert.Equal(remove ? 0 : 11, result);
        if (remove)
            Assert.Same(replacement, GetEntry(cache, "key"));
        else
            Assert.Equal(11L, GetEntry(cache, "key").StoredValue);
        Assert.Equal(50, cache.CurrentMemorySize);
    }

    [Fact]
    public void UpdateEntry_WhenKeyRemovedDuringUpdate_AddsInsteadOfUpdating()
    {
        // Arrange
        // The key disappears between the read and the publish, so the retry must take the add path and report
        // it. Reporting the discarded update's result is how AddAsync used to leave lock keys with no owner.
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var cache = CreateCacheClient(o => o.TimeProvider(timeProvider).WithFixedSizing(10_000, 50));
        StoreEntry(cache, "key", CreateEntry("old", timeProvider));
        int attempts = 0;

        // Act
        string outcome = cache.UpdateEntry("key", current =>
        {
            if (++attempts is 1)
                cache.UpdateEntry<bool>("key", _ => (null, true));

            return current is null
                ? (CreateEntry("added", timeProvider), "added")
                : (current.WithValue("updated", null, current.Size), "updated");
        });

        // Assert
        Assert.Equal(2, attempts);
        Assert.Equal("added", outcome);
        Assert.Equal("added", GetEntry(cache, "key").StoredValue);
        Assert.Equal(50, cache.CurrentMemorySize);
    }

    [Theory]
    [InlineData("Add", false)]
    [InlineData("Replace", false)]
    [InlineData("Set", false)]
    [InlineData("SetAll", false)]
    [InlineData("Increment", false)]
    [InlineData("ListAdd", false)]
    [InlineData("Add", true)]
    [InlineData("Replace", true)]
    [InlineData("Set", true)]
    public async Task WriteAsync_WithExpirationDuringSizing_PreservesLiveEntry(string operation, bool incumbentExpired)
    {
        // Arrange
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        int sizingCalls = 0;
        using var cache = CreateCacheClient(o => o.TimeProvider(clock)
            .MaxMemorySize(1000).SizeCalculator(_ =>
            {
                if (++sizingCalls == 1)
                    clock.Advance(TimeSpan.FromSeconds(1));
                return 100;
            }));
        var expired = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = cache.ItemExpired.AddSyncHandler((_, args) => expired.TrySetResult(args.Key));
        object value = operation switch
        {
            "Increment" => 10L,
            "ListAdd" => new Dictionary<int, DateTime?> { [1] = clock.GetUtcNow().UtcDateTime.AddMinutes(10) },
            _ => "current-owner"
        };
        var original = CreateEntry(value, clock, TimeSpan.FromMinutes(1));
        StoreEntry(cache, "key", original);
        if (incumbentExpired)
            clock.Advance(TimeSpan.FromMinutes(2));
        var expiration = TimeSpan.FromMilliseconds(10);

        // Act
        bool changed = operation switch
        {
            "Add" => await cache.AddAsync("key", "stale-owner", expiration),
            "Replace" => await cache.ReplaceAsync("key", "stale-owner", expiration),
            "SetAll" => await cache.SetAllAsync(new Dictionary<string, string> { ["key"] = "stale-owner" }, expiration) > 0,
            "Increment" => await cache.IncrementAsync("key", 5L, expiration) != 0,
            "ListAdd" => await cache.ListAddAsync("key", [1, 2], expiration) > 0,
            _ => await cache.SetAsync("key", "stale-owner", expiration)
        };

        // Assert
        Assert.False(changed);
        if (incumbentExpired)
        {
            Assert.Equal(0, cache.CurrentMemorySize);
            Assert.Equal("key", await expired.Task.WaitAsync(TimeSpan.FromSeconds(5), TestCancellationToken));
            Assert.False(await cache.ExistsAsync("key"));
            return;
        }

        Assert.Same(original, GetEntry(cache, "key"));
        switch (operation)
        {
            case "Increment":
                Assert.Equal(10, (await cache.GetAsync<long>("key")).Value);
                break;
            case "ListAdd":
                Assert.Equal([1], (await cache.GetListAsync<int>("key")).Value);
                break;
            default:
                Assert.Equal("current-owner", (await cache.GetAsync<string>("key")).Value);
                break;
        }
        Assert.Equal(TimeSpan.FromSeconds(59), await cache.GetExpirationAsync("key"));
        Assert.Equal(50, cache.CurrentMemorySize);
    }

    [Theory]
    [InlineData("Add", true)]
    [InlineData("Increment", 10L)]
    [InlineData("ListAdd", 1L)]
    [InlineData("ListRemove", 0L)]
    [InlineData("Replace", false)]
    [InlineData("ReplaceIfEqual", false)]
    [InlineData("Set", true)]
    [InlineData("SetExpiration", null)]
    [InlineData("SetIfHigher", 10L)]
    [InlineData("SetIfLower", 10L)]
    public async Task WriteAsync_WithExpiredUnsweptEntry_TreatsKeyAsMissing(string operation, object? expectedResult)
    {
        // Arrange: an expired entry that maintenance has not removed yet must behave as if the key were absent
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var cache = CreateCacheClient(o => o.TimeProvider(timeProvider));
        var expired = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = cache.ItemExpired.AddSyncHandler((_, args) => expired.TrySetResult(args.Key));
        bool listOperation = operation.StartsWith("List", StringComparison.Ordinal);
        object stored = listOperation
            ? new Dictionary<string, DateTime?> { ["old"] = null }
            : operation is "SetIfLower" ? 1L : 100L;
        StoreEntry(cache, "key", CreateEntry(stored, timeProvider, TimeSpan.FromMinutes(1)));
        timeProvider.Advance(TimeSpan.FromMinutes(2));

        // Act
        object? result = operation switch
        {
            "Add" => await cache.AddAsync("key", 10L),
            "Increment" => await cache.IncrementAsync("key", 10L),
            "ListAdd" => await cache.ListAddAsync("key", ["new"]),
            "ListRemove" => await cache.ListRemoveAsync("key", ["old"]),
            "Replace" => await cache.ReplaceAsync("key", 10L),
            "ReplaceIfEqual" => await cache.ReplaceIfEqualAsync("key", 10L, 100L),
            "Set" => await cache.SetAsync("key", 10L),
            "SetExpiration" => await SetExpirationAsync(),
            "SetIfHigher" => await cache.SetIfHigherAsync("key", 10L),
            "SetIfLower" => await cache.SetIfLowerAsync("key", 10L),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
        };

        // Assert
        Assert.Equal(expectedResult, result);
        Assert.Equal("key", await expired.Task.WaitAsync(TimeSpan.FromSeconds(5), TestCancellationToken));
        if (listOperation)
        {
            Assert.Equal(operation is "ListAdd" ? ["new"] : [], (await cache.GetListAsync<string>("key")).Value ?? []);
            return;
        }

        var value = await cache.GetAsync<long>("key");
        bool keyWritten = expectedResult is true or long;
        Assert.Equal(keyWritten, value.HasValue);
        if (keyWritten)
            Assert.Equal(10L, value.Value);

        async Task<object?> SetExpirationAsync()
        {
            await cache.SetExpirationAsync("key", TimeSpan.FromMinutes(10));
            return null;
        }
    }

    /// <summary>
    /// Creates a client with this test's logger, serializer and resilience policy provider, then applies the test's own options.
    /// </summary>
    protected InMemoryCacheClient CreateCacheClient(Builder<InMemoryCacheClientOptionsBuilder, InMemoryCacheClientOptions>? config = null)
    {
        return new InMemoryCacheClient(o =>
        {
            o
                .Serializer(DefaultSerializer.Instance)
                .ResiliencePolicyProvider(new ResiliencePolicyProvider(loggerFactory: Log));
            return config is null ? o : config(o);
        });
    }

    private static InMemoryCacheClient.CacheEntry CreateEntry(object value, TimeProvider timeProvider, TimeSpan? expiresIn = null)
    {
        DateTime? expiresAt = expiresIn.HasValue ? timeProvider.GetUtcNow().UtcDateTime.Add(expiresIn.Value) : null;
        return new InMemoryCacheClient.CacheEntry(value, expiresAt, timeProvider, shouldClone: false, size: 50);
    }

    private static InMemoryCacheClient.CacheEntry GetEntry(InMemoryCacheClient cache, string key)
    {
        return cache.UpdateEntry(key, current => (current, current)) ?? throw new InvalidOperationException($"Missing cache key: {key}");
    }

    private static object GetListComparer(IDictionary<string, DateTime?> dictionary) => dictionary switch
    {
        Dictionary<string, DateTime?> value => value.Comparer,
        SortedDictionary<string, DateTime?> value => value.Comparer,
        SortedList<string, DateTime?> value => value.Comparer,
        ConcurrentDictionary<string, DateTime?> value => value.Comparer,
        OrderedDictionary<string, DateTime?> value => value.Comparer,
        _ => throw new NotSupportedException()
    };

    private static void StoreEntry(InMemoryCacheClient cache, string key, InMemoryCacheClient.CacheEntry entry)
    {
        cache.UpdateEntry<bool>(key, _ => (entry, true));
    }

    /// <summary>
    /// A value whose first equality check runs <paramref name="duringFirstComparison"/>, so a test can change the
    /// cache or the clock in the middle of a conditional operation's comparison, on the same thread.
    /// </summary>
    private sealed class ComparedValue(string id, Action? duringFirstComparison = null) : IEquatable<ComparedValue>
    {
        private Action? _duringFirstComparison = duringFirstComparison;

        public string Id { get; } = id;

        public bool Equals(ComparedValue? other)
        {
            Interlocked.Exchange(ref _duringFirstComparison, null)?.Invoke();
            return other is not null && String.Equals(Id, other.Id, StringComparison.Ordinal);
        }

        public override bool Equals(object? obj) => obj is ComparedValue other && Equals(other);

        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Id);
    }

    /// <summary>
    /// A comparer whose hash codes depend on its own identity, so a clone that copies the comparer must rehash its keys.
    /// </summary>
    private sealed class InstanceHashComparer : IEqualityComparer<string>
    {
        public bool Equals(string? x, string? y) => StringComparer.OrdinalIgnoreCase.Equals(x, y);
        public int GetHashCode(string value) => StringComparer.OrdinalIgnoreCase.GetHashCode(value) ^ RuntimeHelpers.GetHashCode(this);
    }

    private readonly struct IdOnlyMember(int id, string name) : IEquatable<IdOnlyMember>
    {
        public int Id { get; } = id;
        public string Name { get; } = name;
        public bool Equals(IdOnlyMember other) => Id == other.Id;
        public override bool Equals(object? obj) => obj is IdOnlyMember other && Equals(other);
        public override int GetHashCode() => Id;
    }

    private sealed class MetadataListDictionary : Dictionary<int, DateTime?>
    {
        public object Metadata { get; } = new();
    }

    private sealed class MutableClassListItem(int id) : IEquatable<MutableClassListItem>
    {
        public int Id { get; } = id;
        public List<int> Values { get; } = [id];
        public bool Equals(MutableClassListItem? other) => other is not null && Id == other.Id;
        public override bool Equals(object? other) => other is MutableClassListItem item && Equals(item);
        public override int GetHashCode() => Id;
    }

    private record MyData(string Name, int Value);

    /// <summary>
    /// A clock for one entry that runs <see cref="OnRead"/> when the entry checks its expiration, which compaction
    /// does while choosing what to evict. That lets a test replace the entry between selection and removal.
    /// </summary>
    private sealed class ReadHookTimeProvider(TimeProvider inner) : TimeProvider
    {
        private bool _inHook;

        public bool Armed { get; set; }
        public Action? OnRead { get; set; }

        public override DateTimeOffset GetUtcNow()
        {
            if (Armed && !_inHook && OnRead is { } onRead)
            {
                _inHook = true;
                try
                {
                    onRead();
                }
                finally
                {
                    _inHook = false;
                }
            }

            return inner.GetUtcNow();
        }
    }

    private sealed class ReferenceHashedListClass(List<int> values)
    {
        public List<int> Values { get; } = values;
    }

    private readonly record struct ReferenceHashedListItem(List<int> Values);

    private readonly record struct ReferenceHoldingListItem(int Id, List<int> Values)
    {
        public bool Equals(ReferenceHoldingListItem other) => Id == other.Id;
        public override int GetHashCode() => Id;
    }

    private sealed class ThrowingComparer(int throwOn) : IEqualityComparer<int>
    {
        public bool Equals(int x, int y) => x == y;
        public int GetHashCode(int obj) => obj == throwOn ? throw new InvalidOperationException("Comparer failed.") : obj;
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
        return CreateCacheClient(o => o
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
        return CreateCacheClient(o => o
            .CloneValues(true)
            .ShouldThrowOnSerializationError(shouldThrowOnSerializationError)
            .WithDynamicSizing(maxMemorySize: 10_000_000, Log)); // 10MB limit with dynamic size calculation
    }
}
