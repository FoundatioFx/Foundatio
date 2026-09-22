using System;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Lock;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Foundatio.Tests.Locks;

public sealed class CacheLockRenewalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RenewAsync_WhenLeaseExpires_RejectsStaleOwner(bool acquiredByAnotherOwner)
    {
        var time = new FakeTimeProvider();
        using var cache = new InMemoryCacheClient(o => o.TimeProvider(time));
        var provider = new CacheLockProvider(cache, null, time);
        await using var stale = await provider.AcquireAsync("migration", TimeSpan.FromMinutes(1), cancellationToken: TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMinutes(2));
        await using var replacement = acquiredByAnotherOwner
            ? await provider.AcquireAsync("migration", TimeSpan.FromMinutes(10), cancellationToken: TestContext.Current.CancellationToken)
            : null;

        await Assert.ThrowsAsync<LockException>(() => stale.RenewAsync());
        Assert.Equal(0, stale.RenewalCount);
        if (replacement is not null)
        {
            await replacement.RenewAsync();
            await stale.ReleaseAsync();
            Assert.True(await provider.IsLockedAsync("migration"));
        }
        else
            Assert.False(await provider.IsLockedAsync("migration"));
    }

    [Fact]
    public async Task RenewAsync_AfterRelease_DoesNotRecreateLease()
    {
        using var cache = new InMemoryCacheClient();
        var provider = new CacheLockProvider(cache, null);
        await using var lease = await provider.AcquireAsync("migration", cancellationToken: TestContext.Current.CancellationToken);
        await lease.ReleaseAsync();
        await Assert.ThrowsAsync<LockException>(() => lease.RenewAsync());
        Assert.False(await provider.IsLockedAsync("migration"));
        Assert.Equal(0, lease.RenewalCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplaceIfEqualAsync_DoesNotReviveExpiredEntries(bool explicitlyRemoved)
    {
        var time = new FakeTimeProvider();
        using var cache = new InMemoryCacheClient(o => o.TimeProvider(time));
        await cache.SetAsync("lease", "owner", TimeSpan.FromMinutes(1));
        if (explicitlyRemoved)
            Assert.True(await cache.RemoveIfEqualAsync("lease", "owner"));
        else
            time.Advance(TimeSpan.FromMinutes(2));

        Assert.False(await cache.ReplaceIfEqualAsync("lease", "owner", "owner", TimeSpan.FromMinutes(10)));
        Assert.False(await cache.ExistsAsync("lease"));
    }

    [Fact]
    public async Task RenewAsync_WithCurrentOwner_ExtendsLease()
    {
        var time = new FakeTimeProvider();
        using var cache = new InMemoryCacheClient(o => o.TimeProvider(time));
        var provider = new CacheLockProvider(cache, null, time);
        await using var lease = await provider.AcquireAsync("migration", TimeSpan.FromMinutes(1), cancellationToken: TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(30));
        await lease.RenewAsync(TimeSpan.FromMinutes(3));
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(await provider.IsLockedAsync("migration"));
        Assert.Equal(1, lease.RenewalCount);
    }
}
