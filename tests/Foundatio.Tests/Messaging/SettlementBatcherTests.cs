using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging.Transport;
using Xunit;

namespace Foundatio.Tests.Messaging;

/// <summary>The group commit transports use to settle several messages in one broker call.</summary>
public class SettlementBatcherTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SendAsync_WithNoCallInFlight_SendsTheEntryAtOnce()
    {
        var calls = new ConcurrentQueue<int[]>();
        var batcher = new SettlementBatcher<int>(10, (entries, _) =>
        {
            calls.Enqueue([.. entries]);
            return Task.FromResult<IReadOnlyList<Exception?>?>(null);
        });

        await batcher.SendAsync(1, Token);

        Assert.Equal([[1]], calls);
    }

    [Fact]
    public async Task SendAsync_WhileACallIsInFlight_SendsTheWaitingEntriesTogetherOnceItReturns()
    {
        var calls = new ConcurrentQueue<int[]>();
        var firstCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var batcher = new SettlementBatcher<int>(10, async (entries, _) =>
        {
            calls.Enqueue([.. entries]);
            if (entries[0] == 0)
                await firstCall.Task;

            return null;
        });

        var first = batcher.SendAsync(0, Token);
        var waiting = Enumerable.Range(1, 4).Select(i => batcher.SendAsync(i, Token)).ToArray();
        Assert.Single(calls);
        Assert.All(waiting, t => Assert.False(t.IsCompleted));

        firstCall.SetResult();
        await Task.WhenAll(waiting.Append(first)).WaitAsync(TimeSpan.FromSeconds(5), Token);

        Assert.Equal([[0], [1, 2, 3, 4]], calls);
    }

    [Fact]
    public async Task SendAsync_WhenABatchFillsWhileACallIsInFlight_SendsItWithoutWaiting()
    {
        var calls = new ConcurrentQueue<int[]>();
        var firstCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var batcher = new SettlementBatcher<int>(3, async (entries, _) =>
        {
            calls.Enqueue([.. entries]);
            if (entries[0] == 0)
                await firstCall.Task;

            return null;
        });

        var first = batcher.SendAsync(0, Token);
        var full = Enumerable.Range(1, 3).Select(i => batcher.SendAsync(i, Token)).ToArray();
        await Task.WhenAll(full).WaitAsync(TimeSpan.FromSeconds(5), Token);

        Assert.False(first.IsCompleted);
        Assert.Equal([[0], [1, 2, 3]], calls);
        firstCall.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(5), Token);
    }

    [Fact]
    public async Task SendAsync_WhenAnEntryFails_FailsOnlyThatEntry()
    {
        var firstCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var batcher = new SettlementBatcher<int>(10, async (entries, _) =>
        {
            if (entries[0] == 0)
            {
                await firstCall.Task;
                return null;
            }

            return entries.Select(e => e == 2 ? new MessageLeaseLostException("lost") : null).ToArray();
        });

        var first = batcher.SendAsync(0, Token);
        var ok = batcher.SendAsync(1, Token);
        var lost = batcher.SendAsync(2, Token);
        firstCall.SetResult();

        await first.WaitAsync(TimeSpan.FromSeconds(5), Token);
        await ok.WaitAsync(TimeSpan.FromSeconds(5), Token);
        await Assert.ThrowsAsync<MessageLeaseLostException>(() => lost.WaitAsync(TimeSpan.FromSeconds(5), Token));
    }

    [Fact]
    public async Task SendAsync_WhenTheCallThrows_FailsEveryEntryOfTheCall()
    {
        var batcher = new SettlementBatcher<int>(10, (_, _) => throw new InvalidOperationException("broker down"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => batcher.SendAsync(1, Token));

        Assert.Equal("broker down", error.Message);
    }

    [Fact]
    public async Task SendAsync_WhenCancelledBeforeItsCall_DropsTheEntry()
    {
        var calls = new ConcurrentQueue<int[]>();
        var firstCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var batcher = new SettlementBatcher<int>(10, async (entries, _) =>
        {
            calls.Enqueue([.. entries]);
            if (entries[0] == 0)
                await firstCall.Task;

            return null;
        });

        var first = batcher.SendAsync(0, Token);
        using var cancellation = new CancellationTokenSource();
        var cancelled = batcher.SendAsync(1, cancellation.Token);
        var kept = batcher.SendAsync(2, Token);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        firstCall.SetResult();
        await Task.WhenAll(first, kept).WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal([[0], [2]], calls);
    }

    [Fact]
    public async Task SendAsync_WhenEveryCallerOfASentCallCancels_CancelsTheCall()
    {
        var callCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var batcher = new SettlementBatcher<int>(10, async (_, ct) =>
        {
            await using (ct.Register(() => callCancelled.TrySetResult()))
                await Task.Delay(Timeout.Infinite, ct);

            return null;
        });

        using var cancellation = new CancellationTokenSource();
        var sending = batcher.SendAsync(1, cancellation.Token);
        await cancellation.CancelAsync();

        await callCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
    }

    [Fact]
    public void Constructor_WithABatchSizeUnderOne_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SettlementBatcher<int>(0, (_, _) => Task.FromResult<IReadOnlyList<Exception?>?>(null)));
    }
}
