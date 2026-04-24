using System;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Lock;
using Foundatio.Messaging;
using Foundatio.Redis.Tests.Extensions;
using Foundatio.Tests.Locks;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using Xunit;

namespace Foundatio.Redis.Tests.Locks;

public class RedisLockTests : LockTestBase, IDisposable, IAsyncLifetime
{
    private readonly string _topic = $"test-lock-{Guid.NewGuid().ToString("N")[..10]}";
    private readonly ICacheClient _cache;
    private readonly IMessageBus _messageBus;
    private readonly RedisProtocol? _protocol;

    protected virtual RedisProtocol? Protocol => _protocol;

    public RedisLockTests(ITestOutputHelper output) : this(output, null) { }

    protected RedisLockTests(ITestOutputHelper output, RedisProtocol? protocol) : base(output)
    {
        _protocol = protocol;
        var muxer = SharedConnection.GetMuxer(Log, _protocol)
            ?? throw new InvalidOperationException("Redis connection is not configured. Set the RedisConnectionString environment variable.");

        _cache = new RedisCacheClient(o => o.ConnectionMultiplexer(muxer).LoggerFactory(Log));
        _messageBus = new RedisMessageBus(o => o.Subscriber(muxer.GetSubscriber()).Topic(_topic).LoggerFactory(Log));
    }

    protected override ILockProvider? GetThrottlingLockProvider(int maxHits, TimeSpan period)
    {
        var muxer = SharedConnection.GetMuxer(Log, Protocol);
        if (muxer is null)
            return null;

        return new ThrottlingLockProvider(_cache, maxHits, period, null, null, Log);
    }

    protected override ILockProvider? GetLockProvider()
    {
        var muxer = SharedConnection.GetMuxer(Log, Protocol);
        if (muxer is null)
            return null;

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

    public override ValueTask InitializeAsync()
    {
        _logger.LogDebug("Initializing");
        var muxer = SharedConnection.GetMuxer(Log, Protocol);
        if (muxer is null)
            return ValueTask.CompletedTask;

        return new ValueTask(muxer.FlushAllAsync());
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        _logger.LogDebug("Disposing");
        Dispose();
    }
}

public class RedisLockResp3Tests : RedisLockTests
{
    public RedisLockResp3Tests(ITestOutputHelper output) : base(output, RedisProtocol.Resp3) { }
}
