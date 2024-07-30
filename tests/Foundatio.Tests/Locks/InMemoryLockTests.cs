using System;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Lock;
using Foundatio.Messaging;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Locks;

public class InMemoryLockTests : LockTestBase, IDisposable
{
    private readonly ICacheClient _cache;
    private readonly IMessageBus _messageBus;

    public InMemoryLockTests(ITestOutputHelper output) : base(output)
    {
        _cache = new InMemoryCacheClient(o => o.LoggerFactory(Log));
        _messageBus = new InMemoryMessageBus(o => o.LoggerFactory(Log));
    }

    protected override ILockProvider GetThrottlingLockProvider(int maxHits, TimeSpan period)
    {
        return new ThrottlingLockProvider(_cache, maxHits, period, null, Log);
    }

    protected override ILockProvider GetLockProvider()
    {
        return new CacheLockProvider(_cache, _messageBus, null, Log);
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

    public void Dispose()
    {
        _cache.Dispose();
        _messageBus.Dispose();
    }
}
