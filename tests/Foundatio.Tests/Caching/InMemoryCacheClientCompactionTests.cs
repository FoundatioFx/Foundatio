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
        Log.SetLogLevel<InMemoryCacheClient>(LogLevel.Trace);
        Log.MaxLogEntriesToWrite = Int32.MaxValue;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DoMaintenanceAsync_WhenCandidatesAreReplaced_ContinuesUntilWithinLimit(bool useMemoryLimit)
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        using var cache = CreateCache(useMemoryLimit, timeProvider);
        SeedEntry(cache, "first", timeProvider);
        SeedEntry(cache, "second", timeProvider);
        const int competingWrites = 25;
        int replacements = 0;
        Log.Options.WriteLogEntryFunc = entry =>
        {
            // Replace the selected entry after selection but before conditional removal, using the existing log sink.
            if (entry.Message.StartsWith("Removing cache entry ", StringComparison.Ordinal) && replacements < competingWrites)
            {
                replacements++;
                ReplaceEntries(cache);
            }
        };

        // Act
        await cache.DoMaintenanceAsync().WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(competingWrites, replacements);
        Assert.Equal(1, cache.Count);
        Assert.Equal(useMemoryLimit ? 50 : 0, cache.CurrentMemorySize);
        Assert.DoesNotContain(Log.LogEntries, entry => entry.LogLevel >= LogLevel.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DoMaintenanceAsync_WithMoreEntriesThanPassBudget_CompactsToLimit(bool useMemoryLimit)
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        using var cache = CreateCache(useMemoryLimit, timeProvider);
        for (int i = 0; i < 1101; i++)
            SeedEntry(cache, i.ToString(), timeProvider);

        // Act
        await cache.DoMaintenanceAsync().WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, cache.Count);
        Assert.Equal(useMemoryLimit ? 50 : 0, cache.CurrentMemorySize);
        Assert.DoesNotContain(Log.LogEntries, entry => entry.LogLevel >= LogLevel.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SetAsync_WhenCompactionBudgetExhausted_ContinuesWithoutAnotherWrite(bool useMemoryLimit)
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        using var cache = CreateCache(useMemoryLimit, timeProvider);
        var maintenanceCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        const int competingWrites = 25;
        int replacements = 0;
        bool simulateContention = false;
        Log.Options.WriteLogEntryFunc = entry =>
        {
            if (simulateContention && entry.Message.StartsWith("Removing cache entry ", StringComparison.Ordinal) && replacements < competingWrites)
            {
                replacements++;
                ReplaceEntries(cache);
            }
            else if (entry.Message == "DoMaintenance: Finished")
            {
                maintenanceCompleted.TrySetResult();
            }
        };
        await cache.SetAsync("first", "value");
        await maintenanceCompleted.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        maintenanceCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SeedEntry(cache, "second", timeProvider);
        simulateContention = true;

        // Act
        bool stored = await cache.SetAsync("first", "updated");
        // Keep fake time frozen: exhaustion must schedule cleanup even inside the maintenance throttle window.
        await maintenanceCompleted.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        // Assert
        Assert.True(stored);
        Assert.Equal(competingWrites, replacements);
        Assert.Equal(1, cache.Count);
        Assert.Equal(useMemoryLimit ? 50 : 0, cache.CurrentMemorySize);
        Assert.DoesNotContain(Log.LogEntries, entry => entry.LogLevel >= LogLevel.Error);
    }

    private InMemoryCacheClient CreateCache(bool useMemoryLimit, FakeTimeProvider timeProvider)
    {
        return new InMemoryCacheClient(o =>
        {
            o.TimeProvider(timeProvider).LoggerFactory(Log);
            return useMemoryLimit ? o.WithFixedSizing(50, 50) : o.MaxItems(1);
        });
    }

    private static void ReplaceEntries(InMemoryCacheClient cache)
    {
        foreach (string key in cache.Keys)
            cache.UpdateEntry<bool>(key, current => current is null
                ? (null, false)
                : (current.WithValue("replacement", current.ExpiresAt, current.Size), true));
    }

    private static void SeedEntry(InMemoryCacheClient cache, string key, FakeTimeProvider timeProvider)
    {
        var entry = new InMemoryCacheClient.CacheEntry("value", null, timeProvider, shouldClone: false, size: 50);
        cache.UpdateEntry<bool>(key, _ => (entry, true));
    }
}
