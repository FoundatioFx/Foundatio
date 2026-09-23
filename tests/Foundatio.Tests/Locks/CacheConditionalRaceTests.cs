using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Xunit;

namespace Foundatio.Tests.Locks;

public sealed class CacheConditionalRaceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConditionalMutation_DoesNotReportSuccessAfterAnotherOwnerReplacesTheEntry(bool remove)
    {
        using var cache = new InMemoryCacheClient(o => o.CloneValues(false));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var original = new GateValue("original", () =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Test did not release the paused comparison.");
        });
        var replacementOwner = new GateValue("replacement");
        await cache.SetAsync("lease", original);
        var pending = Task.Run(() => remove
            ? cache.RemoveIfEqualAsync("lease", original)
            : cache.ReplaceIfEqualAsync("lease", new GateValue("renewed"), original), TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await cache.SetAsync("lease", replacementOwner);
        }
        finally
        {
            release.Set();
        }
        Assert.False(await pending);
        Assert.Same(replacementOwner, (await cache.GetAsync<GateValue>("lease")).Value);
    }

    [Fact]
    public async Task ReplaceIfEqualAsync_DoesNotRenewEntryThatExpiresDuringComparison()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        using var cache = new InMemoryCacheClient(o => o.TimeProvider(time).CloneValues(false));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var original = new GateValue("owner", () =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Test did not release the paused comparison.");
        });

        await cache.SetAsync("lease", original, TimeSpan.FromMinutes(1));
        var pending = Task.Run(
            () => cache.ReplaceIfEqualAsync("lease", new GateValue("renewed"), original, TimeSpan.FromMinutes(10)),
            TestContext.Current.CancellationToken);

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            time.Advance(TimeSpan.FromMinutes(2));
        }
        finally
        {
            release.Set();
        }

        Assert.False(await pending);
        Assert.False(await cache.ExistsAsync("lease"));
    }

    private sealed class GateValue(string id, Action? beforeEquals = null) : IEquatable<GateValue>
    {
        public string Id { get; } = id;
        public bool Equals(GateValue? other)
        {
            beforeEquals?.Invoke();
            return other is not null && String.Equals(Id, other.Id, StringComparison.Ordinal);
        }
        public override bool Equals(object? obj) => obj is GateValue other && Equals(other);
        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Id);
    }
}
