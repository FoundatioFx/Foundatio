using Foundatio.Caching;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;
using Xunit;

namespace Foundatio.Tests.Caching;

/// <summary>Runs the hybrid cache suite with invalidations flowing through the legacy bus adapter over the new transport.</summary>
public class LegacyAdapterHybridCacheClientTests : InMemoryHybridCacheClientTests
{
    private readonly IMessageBus _adapter;

    public LegacyAdapterHybridCacheClientTests(ITestOutputHelper output) : base(output)
    {
        _adapter = new LegacyMessageBusAdapter(new InMemoryMessageTransport(), new SharedMessageBusOptions { LoggerFactory = Log });
    }

    protected override ICacheClient GetCacheClient(bool shouldThrowOnSerializationError = true)
    {
        return new InMemoryHybridCacheClient(_adapter, Log, shouldThrowOnSerializationError);
    }

    protected override HybridCacheClient GetDistributedHybridCacheClient(bool shouldThrowOnSerializationError = true)
    {
        return new InMemoryHybridCacheClient(_distributedCache, _adapter, Log, shouldThrowOnSerializationError);
    }
}
