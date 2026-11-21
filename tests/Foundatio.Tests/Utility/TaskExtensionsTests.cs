using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;
using Xunit;

namespace Foundatio.Tests.Utility;

public class TaskExtensionsTests
{
    [Fact]
    public async Task SafeDelay_WhenCancelled_CompletesWithoutThrowing()
    {
        using var cancellation = new CancellationTokenSource();
        var delay = TimeProvider.System.SafeDelay(TimeSpan.FromMinutes(5), cancellation.Token);
        Assert.False(delay.IsCompleted);

        await cancellation.CancelAsync();
        await delay.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(delay.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task SafeDelay_AlreadyCancelled_CompletesWithoutThrowing()
    {
        var delay = TimeProvider.System.SafeDelay(TimeSpan.FromMinutes(5), new CancellationToken(true));
        await delay;

        Assert.True(delay.IsCompletedSuccessfully);
    }

    [Fact]
    public Task SafeDelay_WithElapsedDelay_Completes()
    {
        return TimeProvider.System.SafeDelay(TimeSpan.FromMilliseconds(1), TestContext.Current.CancellationToken);
    }

    [Fact]
    public Task SafeDelay_WithInvalidDelay_Throws()
    {
        return Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => TimeProvider.System.SafeDelay(TimeSpan.FromMilliseconds(-5), TestContext.Current.CancellationToken));
    }
}
