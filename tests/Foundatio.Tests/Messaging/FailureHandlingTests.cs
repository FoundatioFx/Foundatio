using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Xunit;

namespace Foundatio.Tests.Messaging;

public class FailureHandlingTests
{
    [Fact]
    public async Task DeadLetterOn_MatchingException_DeadLettersOnFirstAttemptAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var transport = new InMemoryMessageTransport();
        await using var bus = new MessageBus(transport);
        int attempts = 0;

        await using var subscription = await bus.SubscribeAsync<FailingItem>((_, _) =>
        {
            Interlocked.Increment(ref attempts);
            throw new ArgumentException("bad data a retry can never fix");
        }, new MessageSubscriptionOptions { MaxAttempts = 5 }.DeadLetterOn<ArgumentException>(), cancellationToken);

        await bus.SendAsync(new FailingItem { Data = "poison" }, cancellationToken: cancellationToken);

        var stats = await WaitForDeadLetterAsync(transport, "failing-item", cancellationToken);
        Assert.Equal(1, stats.Deadletter);
        Assert.Equal(1, Volatile.Read(ref attempts)); // never retried

        var dead = Assert.Single(await transport.ReceiveDeadLetteredAsync("failing-item", new ReceiveRequest { MaxMessages = 10 }, cancellationToken));
        Assert.Equal("unrecoverable:ArgumentException", dead.Headers[KnownHeaders.DeadLetterReason]);
    }

    [Fact]
    public async Task DeadLetterWhen_GlobalPolicy_AppliesWhenSubscriptionDoesNotOverrideAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var transport = new InMemoryMessageTransport();
        await using var bus = new MessageBus(transport, new MessageBusOptions
        {
            RetryPolicy = new RetryPolicy { DeadLetterWhen = ex => ex is InvalidOperationException }
        });
        int attempts = 0;

        await using var subscription = await bus.SubscribeAsync<FailingItem>((_, _) =>
        {
            Interlocked.Increment(ref attempts);
            throw new InvalidOperationException("unrecoverable per global policy");
        }, cancellationToken: cancellationToken);

        await bus.SendAsync(new FailingItem { Data = "poison" }, cancellationToken: cancellationToken);

        var stats = await WaitForDeadLetterAsync(transport, "failing-item", cancellationToken);
        Assert.Equal(1, stats.Deadletter);
        Assert.Equal(1, Volatile.Read(ref attempts));
    }

    [Fact]
    public async Task DeadLetter_StampsForensicsHeadersAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var transport = new InMemoryMessageTransport();
        await using var bus = new MessageBus(transport);

        await using var subscription = await bus.SubscribeAsync<FailingItem>((_, _) =>
            throw new InvalidOperationException("the failure detail"),
            new MessageSubscriptionOptions { MaxAttempts = 1 }, cancellationToken);

        await bus.SendAsync(new FailingItem { Data = "doomed" }, cancellationToken: cancellationToken);

        await WaitForDeadLetterAsync(transport, "failing-item", cancellationToken);
        var dead = Assert.Single(await transport.ReceiveDeadLetteredAsync("failing-item", new ReceiveRequest { MaxMessages = 10 }, cancellationToken));

        Assert.Equal(typeof(InvalidOperationException).FullName, dead.Headers[KnownHeaders.DeadLetterExceptionType]);
        Assert.Equal("the failure detail", dead.Headers[KnownHeaders.DeadLetterExceptionMessage]);
        Assert.NotEmpty(dead.Headers[KnownHeaders.DeadLetterExceptionStackTrace]);
        Assert.Equal("failing-item", dead.Headers[KnownHeaders.DeadLetterOriginalDestination]);
        Assert.NotEmpty(dead.Headers[KnownHeaders.DeadLetterFailedAt]);
        Assert.Equal("1", dead.Headers[KnownHeaders.Attempts]);
    }

    [Fact]
    public void DefaultBackoff_MatchesTheConvergedCurve()
    {
        // Immediate first retry, then 10s/20s/30s (capped) with ±20% jitter.
        Assert.Equal(TimeSpan.Zero, RetryPolicy.DefaultBackoff(1));

        foreach ((int attempt, double expectedSeconds) in new[] { (2, 10d), (3, 20d), (4, 30d), (7, 30d) })
        {
            var delay = RetryPolicy.DefaultBackoff(attempt);
            Assert.InRange(delay.TotalSeconds, expectedSeconds * 0.8, expectedSeconds * 1.2);
        }

        // The default policy uses the curve.
        Assert.Same(RetryPolicy.DefaultBackoff, new RetryPolicy().Backoff);
    }

    private static async Task<MessageDestinationStats> WaitForDeadLetterAsync(InMemoryMessageTransport transport, string destination, CancellationToken cancellationToken)
    {
        var stats = await transport.GetStatsAsync(destination, cancellationToken);
        long deadline = Environment.TickCount64 + 10_000;
        while (stats.Deadletter == 0 && Environment.TickCount64 < deadline)
        {
            await Task.Delay(25, cancellationToken);
            stats = await transport.GetStatsAsync(destination, cancellationToken);
        }

        return stats;
    }

    [MessageRoute("failing-item")]
    private sealed class FailingItem
    {
        public string? Data { get; set; }
    }
}
