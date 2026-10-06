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
        var timeProvider = new FakeTimeProvider();
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
}
