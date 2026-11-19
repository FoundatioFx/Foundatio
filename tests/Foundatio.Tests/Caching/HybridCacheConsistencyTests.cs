using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Foundatio.Tests.Caching;

public class HybridCacheConsistencyTests(ITestOutputHelper output) : HybridCacheClientTestBase(output)
{
    [Fact]
    public async Task GetAsync_WhenSameClientWritesDuringReadThrough_DoesNotCacheStaleValue()
    {
        // Arrange
        const string key = "read-through-race";
        using var distributed = new DelayedListCache(Log);
        using var cache = CreateCache(distributedCache: distributed);
        await distributed.SetAsync(key, 1);
        distributed.AfterNextRead(async () => Assert.True(await cache.SetAsync(key, 2)));

        // Act
        var staleRead = await cache.GetAsync<int>(key).WaitAsync(TimeSpan.FromSeconds(5), TestCancellationToken);
        var nextRead = await cache.GetAsync<int>(key);

        // Assert
        Assert.Equal(1, staleRead.Value);
        Assert.Equal(2, nextRead.Value);
        Assert.Equal(2, (await distributed.GetAsync<int>(key)).Value);
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

    [Fact]
    public async Task GetListAsync_WhenSameClientAddsDuringReadThrough_NextReadIncludesNewMember()
    {
        // Arrange
        const string key = "list-read-through-race";
        using var distributed = new DelayedListCache(Log);
        using var cache = CreateCache(distributedCache: distributed);
        await distributed.ListAddAsync(key, [1]);
        distributed.AfterNextRead(async () => Assert.Equal(1, await cache.ListAddAsync(key, [2])));

        // Act
        var staleRead = await cache.GetListAsync<int>(key).WaitAsync(TimeSpan.FromSeconds(5), TestCancellationToken);
        var nextRead = await cache.GetListAsync<int>(key);

        // Assert
        Assert.Equal([1], staleRead.Value);
        Assert.Equal([1, 2], nextRead.Value.Order());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListAddAsync_WhenColdWritesCompleteOutOfOrder_PreservesBothMembers(bool useSecondClient)
    {
        // Arrange
        const string key = "overlapping-cold-adds";
        using var distributed = new DelayedListCache(Log);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListAddAsync_WhenOnlyDistributedListExists_PreservesEarlierMembers(bool singleString)
    {
        // Arrange
        using var cache = CreateCache();
        const string key = "cold-list";
        await _distributedCache.ListAddAsync(key, ["earlier"]);

        // Act
        long added = singleString ? await cache.ListAddAsync(key, "later") : await cache.ListAddAsync(key, ["later"]);

        // Assert
        Assert.Equal(1, added);
        Assert.Equal(["earlier", "later"], (await cache.GetListAsync<string>(key)).Value.Order());
    }

    [Theory]
    [InlineData("key", false)]
    [InlineData("prefix", false)]
    [InlineData("null-prefix", false)]
    [InlineData("all", false)]
    [InlineData("member", false)]
    [InlineData("key", true)]
    [InlineData("prefix", true)]
    [InlineData("all", true)]
    [InlineData("member", true)]
    public async Task ListAddAsync_WhenRemovedBeforeResponse_KeepsLocalEntryEvicted(string removal, bool useSecondClient)
    {
        // Arrange
        const string key = "pending:list";
        using var distributed = new DelayedListCache(Log);
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
        using var distributed = new DelayedListCache(Log);
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
        using var distributed = new DelayedListCache(Log, clock);
        using var cache = CreateCache(new InMemoryCacheClientOptions { TimeProvider = clock, LoggerFactory = Log }, distributed);
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
        // Arrange
        bool rejectLocalWrites = false;
        using var cache = CreateCache(new InMemoryCacheClientOptions
        {
            LoggerFactory = Log,
            MaxMemorySize = 1000,
            MaxEntrySize = 10,
            ShouldThrowOnMaxEntrySizeExceeded = shouldThrow,
            SizeCalculator = _ => !rejectLocalWrites ? 3 : throw new InvalidOperationException("Sizing failed")
        });
        using var peer = CreateCache();
        const string key = "rejected-local-list-write";
        Assert.Equal(2, await cache.ListAddAsync(key, ["old", "keep"]));
        Assert.Equal(["keep", "old"], (await cache.LocalCache.GetListAsync<string>(key)).Value!.Order());
        Assert.Equal(["keep", "old"], (await peer.GetListAsync<string>(key)).Value!.Order());
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
            rejectLocalWrites = false;
        }
        else
        {
            Assert.Equal(1, await write);
        }

        string[] expected = remove ? ["keep"] : ["keep", "new", "old"];
        Assert.Equal(expected, (await _distributedCache.GetListAsync<string>(key)).Value!.Order());
        Assert.Equal(expected, (await cache.GetListAsync<string>(key)).Value!.Order());
        Assert.Equal(expected, (await peer.GetListAsync<string>(key)).Value!.Order());
    }

    [Fact]
    public async Task SetAsync_WhenLocalWriteAndInvalidationThrow_PreservesWriteError()
    {
        // Arrange
        using var cache = CreateCache(new InMemoryCacheClientOptions
        {
            LoggerFactory = Log,
            MaxMemorySize = 1000,
            MaxEntrySize = 10,
            ShouldThrowOnMaxEntrySizeExceeded = true,
            SizeCalculator = value => value is "old" ? 3 : throw new InvalidOperationException("Sizing failed")
        });
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
    public async Task SetAsync_WhenReplacementExpiresDuringLocalSizing_DoesNotReturnOldValue()
    {
        // Arrange
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var distributedCache = new InMemoryCacheClient(o => o.TimeProvider(clock).LoggerFactory(Log));
        using var messageBus = new InMemoryMessageBus(o => o.TimeProvider(clock).LoggerFactory(Log));
        using var cache = CreateCache(new InMemoryCacheClientOptions
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
        }, distributedCache, messageBus);
        const string key = "expired-local-write";
        Assert.True(await cache.SetAsync(key, "old", TimeSpan.FromMinutes(1)));

        // Act
        Assert.True(await cache.SetAsync(key, "replacement", TimeSpan.FromMilliseconds(10)));

        // Assert
        Assert.False((await distributedCache.GetAsync<string>(key)).HasValue);
        Assert.False((await cache.GetAsync<string>(key)).HasValue);
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
        // Arrange
        bool rejectLocalWrites = false;
        using var cache = CreateCache(new InMemoryCacheClientOptions
        {
            LoggerFactory = Log,
            MaxMemorySize = 1000,
            MaxEntrySize = 10,
            ShouldThrowOnMaxEntrySizeExceeded = shouldThrow,
            SizeCalculator = value => !rejectLocalWrites || value is 42L ? 3 : throw new InvalidOperationException("Sizing failed")
        });
        using var peer = CreateCache();
        const string key = "rejected-local-write";
        Assert.True(await cache.SetAsync(key, 10L));
        Assert.Equal(10L, (await cache.LocalCache.GetAsync<long>(key)).Value);
        Assert.Equal(10L, (await peer.GetAsync<long>(key)).Value);
        if (String.Equals(operation, "Add", StringComparison.Ordinal))
            Assert.True(await _distributedCache.RemoveAsync(key));
        rejectLocalWrites = true;

        // Act
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

        // Assert
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
        Assert.Equal(expected, (await _distributedCache.GetAsync<long>(key)).Value);
        Assert.Equal(expected, (await cache.GetAsync<long>(key)).Value);
        if (!String.Equals(operation, "Add", StringComparison.Ordinal))
            Assert.Equal(expected, (await peer.GetAsync<long>(key)).Value);
        if (String.Equals(operation, "SetAll", StringComparison.Ordinal))
            Assert.Equal(42L, (await cache.GetAsync<long>("accepted-local-write")).Value);
    }

    private HybridCacheClient CreateCache(InMemoryCacheClientOptions? options = null, ICacheClient? distributedCache = null, IMessageBus? messageBus = null)
    {
        return new HybridCacheClient(distributedCache ?? _distributedCache, messageBus ?? _messageBus, options, Log);
    }

    private sealed class DelayedListCache(ILoggerFactory loggerFactory, TimeProvider? clock = null) : InMemoryCacheClient(o => o.LoggerFactory(loggerFactory).TimeProvider(clock ?? TimeProvider.System)), ICacheClient
    {
        private Func<Task>? _afterNextRead;
        private Func<Task>? _afterNextWrite;

        public void AfterNextRead(Func<Task> action) => _afterNextRead = action;

        public void AfterNextWrite(Func<Task> action) => _afterNextWrite = action;

        async Task<CacheValue<T>> ICacheClient.GetAsync<T>(string key)
        {
            var value = await GetAsync<T>(key);
            await RunOnceAsync(ref _afterNextRead);
            return value;
        }

        async Task<CacheValue<ICollection<T>>> ICacheClient.GetListAsync<T>(string key, int? page, int pageSize)
        {
            var value = await GetListAsync<T>(key, page, pageSize);
            await RunOnceAsync(ref _afterNextRead);
            return value;
        }

        async Task<long> ICacheClient.ListAddAsync<T>(string key, IEnumerable<T> values, TimeSpan? expiresIn)
        {
            long added = await ListAddAsync(key, values, expiresIn);
            await RunOnceAsync(ref _afterNextWrite);
            return added;
        }

        private static Task RunOnceAsync(ref Func<Task>? action)
        {
            var callback = Interlocked.Exchange(ref action, null);
            return callback is null ? Task.CompletedTask : callback();
        }
    }
}
