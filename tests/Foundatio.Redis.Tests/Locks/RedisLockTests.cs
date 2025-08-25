using System;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Lock;
using Foundatio.Messaging;
using Foundatio.Redis.Tests.Extensions;
using Foundatio.Tests.Locks;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using IAsyncLifetime = Xunit.IAsyncLifetime;

namespace Foundatio.Redis.Tests.Locks;

public class RedisLockTests : LockTestBase, IDisposable, IAsyncLifetime
{
    private readonly ICacheClient _cache;
    private readonly IMessageBus _messageBus;

    public RedisLockTests(ITestOutputHelper output) : base(output)
    {
        var muxer = SharedConnection.GetMuxer(Log);
        _cache = new RedisCacheClient(o => o.ConnectionMultiplexer(muxer).LoggerFactory(Log));
        _messageBus = new RedisMessageBus(o => o.Subscriber(muxer.GetSubscriber()).Topic("test-lock").LoggerFactory(Log));
    }

    protected override ILockProvider GetThrottlingLockProvider(int maxHits, TimeSpan period)
    {
        return new ThrottlingLockProvider(_cache, maxHits, period, null, null, Log);
    }

    protected override ILockProvider GetLockProvider()
    {
        return new CacheLockProvider(_cache, _messageBus, null, null, Log);
    }

    [Fact]
    public override Task CanAcquireAndReleaseLockAsync()
    {
        return base.CanAcquireAndReleaseLockAsync();
    }

    [Fact]
    public override Task LockWillTimeoutAsync()
    {
        return base.LockWillTimeoutAsync();
    }

    [Fact]
    public override Task LockOneAtATimeAsync()
    {
        return base.LockOneAtATimeAsync();
    }

    [Fact]
    public override Task CanAcquireMultipleResources()
    {
        return base.CanAcquireMultipleResources();
    }

    [Fact]
    public override Task CanAcquireLocksInParallel()
    {
        return base.CanAcquireLocksInParallel();
    }

    [Fact]
    public override Task CanAcquireScopedLocksInParallel()
    {
        return base.CanAcquireScopedLocksInParallel();
    }

    [Fact]
    public override Task CanAcquireMultipleLocksInParallel()
    {
        return base.CanAcquireMultipleLocksInParallel();
    }

    [Fact]
    public override Task CanAcquireMultipleScopedResources()
    {
        return base.CanAcquireMultipleScopedResources();
    }

    [Fact]
    public override Task WillThrottleCallsAsync()
    {
        return base.WillThrottleCallsAsync();
    }

    [Fact]
    public override Task CanReleaseLockMultipleTimes()
    {
        return base.CanReleaseLockMultipleTimes();
    }

    [Fact]
    public override Task LockWontTimeoutEarly()
    {
        return base.LockWontTimeoutEarly();
    }

    public void Dispose()
    {
        _cache.Dispose();
        _messageBus.Dispose();
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
        Dispose();

        return Task.CompletedTask;
    }
}
