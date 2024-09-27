using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Messaging;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Caching;

public class InMemoryHybridCacheClientTests : HybridCacheClientTests
{
    public InMemoryHybridCacheClientTests(ITestOutputHelper output) : base(output) { }

    protected override ICacheClient GetCacheClient(bool shouldThrowOnSerializationError = true)
    {
        return new InMemoryHybridCacheClient(_messageBus, Log, shouldThrowOnSerializationError);
    }

    [Fact]
    public override Task CanSetAndGetValueAsync()
    {
        return base.CanSetAndGetValueAsync();
    }

    [Fact]
    public override Task CanSetAndGetObjectAsync()
    {
        return base.CanSetAndGetObjectAsync();
    }

    [Fact]
    public override Task CanTryGetAsync()
    {
        return base.CanTryGetAsync();
    }

    [Fact]
    public override Task CanRemoveByPrefixAsync()
    {
        return base.CanRemoveByPrefixAsync();
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
    public override Task CanUseScopedCachesAsync()
    {
        return base.CanUseScopedCachesAsync();
    }

    [Fact]
    public override Task CanSetExpirationAsync()
    {
        return base.CanSetExpirationAsync();
    }

    [Fact]
    public override Task CanManageListsAsync()
    {
        return base.CanManageListsAsync();
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

    [Fact(Skip = "Performance Test")]
    public override Task MeasureThroughputAsync()
    {
        return base.MeasureThroughputAsync();
    }

    [Fact(Skip = "Performance Test")]
    public override Task MeasureSerializerSimpleThroughputAsync()
    {
        return base.MeasureSerializerSimpleThroughputAsync();
    }

    [Fact(Skip = "Performance Test")]
    public override Task MeasureSerializerComplexThroughputAsync()
    {
        return base.MeasureSerializerComplexThroughputAsync();
    }
}

public class InMemoryHybridCacheClient : HybridCacheClient
{
    public InMemoryHybridCacheClient(IMessageBus messageBus, ILoggerFactory loggerFactory, bool shouldThrowOnSerializationError)
        : base(new InMemoryCacheClient(o => o.LoggerFactory(loggerFactory).ShouldThrowOnSerializationError(shouldThrowOnSerializationError)), messageBus, new InMemoryCacheClientOptions
        {
            CloneValues = true,
            ShouldThrowOnSerializationError = shouldThrowOnSerializationError
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
