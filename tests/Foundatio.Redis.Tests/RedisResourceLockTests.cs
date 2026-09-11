using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Lock;
using Xunit;

namespace Foundatio.Redis.Tests;

public class RedisResourceLockTests
{
    [Fact]
    public async Task ExpiredOwner_CannotReleaseOrRenewANewOwnersLock()
    {
        var connection = RedisTestConnection.Multiplexer;
        Assert.SkipWhen(connection is null, "FOUNDATIO_REDIS_CONNECTION_STRING not set.");
        var provider = new RedisLockProvider(connection!, $"native-lock:{Guid.NewGuid():N}:");
        var token = TestContext.Current.CancellationToken;
        await using var first = await provider.AcquireAsync("report", TimeSpan.FromMilliseconds(100), cancellationToken: token);
        await Task.Delay(150, token);
        await using var second = await provider.AcquireAsync("report", TimeSpan.FromSeconds(10), cancellationToken: token);
        await Assert.ThrowsAsync<LockOwnershipLostException>(() => first.RenewAsync());
        await first.ReleaseAsync();
        Assert.True(await provider.IsLockedAsync("report"));
        await second.RenewAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(1, second.RenewalCount);
        await second.ReleaseAsync();
        Assert.False(await provider.IsLockedAsync("report"));
    }

    [Fact]
    public async Task ContendedAcquisition_StopsWaitingWhenCancelled()
    {
        var connection = RedisTestConnection.Multiplexer;
        Assert.SkipWhen(connection is null, "FOUNDATIO_REDIS_CONNECTION_STRING not set.");
        var provider = new RedisLockProvider(connection!, $"native-lock:{Guid.NewGuid():N}:");
        await using var held = await provider.AcquireAsync("report", cancellationToken: TestContext.Current.CancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(100));
        Assert.Null(await provider.TryAcquireAsync("report", cancellationToken: timeout.Token));
        Assert.True(await provider.IsLockedAsync("report"));
    }
}
