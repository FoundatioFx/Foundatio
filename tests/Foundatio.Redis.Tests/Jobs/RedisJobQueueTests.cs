using System;
using System.Threading.Tasks;
using Foundatio.Queues;
using Foundatio.Redis.Tests.Extensions;
using Foundatio.Tests.Jobs;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using Xunit;

namespace Foundatio.Redis.Tests.Jobs;

public class RedisJobQueueTests : JobQueueTestsBase, IAsyncLifetime
{
    protected virtual RedisProtocol? Protocol => null;

    public RedisJobQueueTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override IQueue<SampleQueueWorkItem> GetSampleWorkItemQueue(int retries, TimeSpan retryDelay)
    {
        var muxer = SharedConnection.GetMuxer(Log, Protocol);
        if (muxer is null)
            return null!;

        return new RedisQueue<SampleQueueWorkItem>(o => o
            .ConnectionMultiplexer(muxer)
            .Retries(retries)
            .RetryDelay(retryDelay)
            .LoggerFactory(Log)
        );
    }

    [Fact]
    public override Task CanRunMultipleQueueJobsAsync()
    {
        return base.CanRunMultipleQueueJobsAsync();
    }

    [Fact]
    public override Task CanRunQueueJobWithLockFailAsync()
    {
        return base.CanRunQueueJobWithLockFailAsync();
    }

    [Fact]
    public override Task CanRunQueueJobAsync()
    {
        return base.CanRunQueueJobAsync();
    }

    [Fact]
    public override Task ActivityWillFlowThroughQueueJobAsync()
    {
        return base.ActivityWillFlowThroughQueueJobAsync();
    }

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _logger.LogDebug("Initializing");
        var muxer = SharedConnection.GetMuxer(Log, Protocol);
        if (muxer is null)
            return;

        await muxer.FlushAllAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        _logger.LogDebug("Disposing");
    }
}

public class RedisJobQueueResp3Tests : RedisJobQueueTests
{
    public RedisJobQueueResp3Tests(ITestOutputHelper output) : base(output) { }
    protected override RedisProtocol? Protocol => RedisProtocol.Resp3;
}
