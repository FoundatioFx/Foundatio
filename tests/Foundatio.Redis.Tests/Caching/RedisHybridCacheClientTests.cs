using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Redis.Tests.Extensions;
using Foundatio.Tests.Caching;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Redis.Tests.Caching;

public class RedisHybridCacheClientTests : HybridCacheClientTests
{
    public RedisHybridCacheClientTests(ITestOutputHelper output) : base(output)
    {
        var muxer = SharedConnection.GetMuxer(Log);
        muxer.FlushAllAsync().GetAwaiter().GetResult();
    }

    protected override ICacheClient GetCacheClient(bool shouldThrowOnSerializationError = true)
    {
        return new RedisHybridCacheClient(o => o
                .ConnectionMultiplexer(SharedConnection.GetMuxer(Log))
                .LoggerFactory(Log).ShouldThrowOnSerializationError(shouldThrowOnSerializationError),
            localConfig => localConfig
                .CloneValues(true)
                .ShouldThrowOnSerializationError(shouldThrowOnSerializationError));
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
    [InlineData(5000)]
    [InlineData(50000)]
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

    [Fact]
    public override Task WillUseLocalCache()
    {
        return base.WillUseLocalCache();
    }

    [Fact(Skip = "Skipping for now until we figure out a timing issue")]
    public override Task WillExpireRemoteItems()
    {
        Log.DefaultMinimumLevel = LogLevel.Trace;
        return base.WillExpireRemoteItems();
    }

    [Fact]
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
