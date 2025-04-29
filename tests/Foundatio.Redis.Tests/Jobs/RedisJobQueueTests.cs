using System;
using System.Threading.Tasks;
using Foundatio.Queues;
using Foundatio.Redis.Tests.Extensions;
using Foundatio.Tests.Jobs;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using IAsyncLifetime = Xunit.IAsyncLifetime;

namespace Foundatio.Redis.Tests.Jobs;

public class RedisJobQueueTests : JobQueueTestsBase, IAsyncLifetime
{
    public RedisJobQueueTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override IQueue<SampleQueueWorkItem> GetSampleWorkItemQueue(int retries, TimeSpan retryDelay)
    {
        return new RedisQueue<SampleQueueWorkItem>(o => o
            .ConnectionMultiplexer(SharedConnection.GetMuxer(Log))
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

    public Task InitializeAsync()
    {
        _logger.LogDebug("Initializing");
        var muxer = SharedConnection.GetMuxer(Log);
        return muxer.FlushAllAsync();
    }

    public Task DisposeAsync()
    {
        _logger.LogDebug("Disposing");
        return Task.CompletedTask;
    }
}
