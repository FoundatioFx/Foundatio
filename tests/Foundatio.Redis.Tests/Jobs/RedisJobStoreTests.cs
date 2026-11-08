using Foundatio.Jobs.Store;
using Foundatio.Tests.Jobs;
using Xunit;

namespace Foundatio.Redis.Tests.Jobs;

public class RedisJobStoreTests(ITestOutputHelper output) : JobStoreTestsBase(output)
{
    protected override IJobStore CreateStore()
    {
        var muxer = SharedConnection.GetMuxer(Log);
        Assert.SkipWhen(muxer is null, "RedisConnectionString is not configured.");
        return new RedisJobStore(muxer, new RedisJobStoreOptions { KeyPrefix = "foundatio-tests:jobs" });
    }
}
