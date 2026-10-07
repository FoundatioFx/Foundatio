using System;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Xunit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Foundatio.Tests.Caching;

public class InMemoryCacheClientCompactionTests : TestWithLoggingBase
{
    public InMemoryCacheClientCompactionTests(ITestOutputHelper output) : base(output)
    {
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
        using var cache = new InMemoryCacheClient(o =>
        {
            o.TimeProvider(timeProvider).LoggerFactory(Log);
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

    [Fact]
    public async Task DoMaintenanceAsync_WhenEvictionCandidateIsReplaced_DoesNotSpendRemovalBudget()
    {
        // Arrange: 10 entries over the limit, so the pass's budget is 20 removals. The oldest entry is replaced
        // after compaction selects it, 15 times. Losing those races frees nothing, so the pass must still make the
        // 10 removals it needs instead of stopping after 20 attempts.
        const int maxItems = 100;
        const int replacementsDuringSelection = 15;
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var cache = new InMemoryCacheClient(o => o.TimeProvider(timeProvider).MaxItems(maxItems).LoggerFactory(Log));
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
}
