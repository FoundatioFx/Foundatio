using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Xunit;

namespace Foundatio.Redis.Tests;

/// <summary>
/// End-to-end tests for the Redis Streams transport that the cross-transport conformance suite can't express: at-least-once
/// recovery across two consumer instances, the core's retry/dead-letter machinery driving the transport, and topic
/// fan-out through the <see cref="PubSub"/> facade. Gated on <c>FOUNDATIO_REDIS_CONNECTION_STRING</c>; unique key prefix
/// per test.
/// </summary>
public class RedisStreamsTransportIntegrationTests
{
    private static RedisStreamsMessageTransport CreateTransport(StackExchange.Redis.IConnectionMultiplexer connection, string prefix, string? consumer = null) =>
        new(new RedisStreamsMessageTransportOptions
        {
            ConnectionMultiplexer = connection,
            KeyPrefix = prefix,
            ConsumerName = consumer
        });

    private static string NewPrefix() => $"fnd-it:{Guid.NewGuid():N}:";

    [Fact]
    public async Task CrashedConsumer_LeaseLapses_AnotherInstanceReclaimsAndCompletesAsync()
    {
        if (RedisTestConnection.Multiplexer is not { } connection)
        {
            Assert.Skip("FOUNDATIO_REDIS_CONNECTION_STRING not set.");
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        string prefix = NewPrefix();
        var visibility = TimeSpan.FromSeconds(2);

        // Both instances share the same key prefix so they operate on the same streams (the lease lives in Redis).
        await using var nodeA = CreateTransport(connection, prefix, "node-a");
        await using var nodeB = CreateTransport(connection, prefix, "node-b");

        await nodeA.EnsureAsync([new DestinationDeclaration { Name = "work", Role = DestinationRole.Queue }], ct);
        await nodeA.SendAsync("work", [Message("survive-me")], new TransportSendOptions(), ct);

        // node-a receives and then "crashes" — it never settles the message.
        var heldByA = Assert.Single(await nodeA.ReceiveAsync("work", new ReceiveRequest { MaxWaitTime = TimeSpan.FromSeconds(2) }, visibility, ct));
        Assert.Equal(1, heldByA.DeliveryCount);

        // While node-a's lease is live, node-b must not see it.
        Assert.Empty(await nodeB.ReceiveAsync("work", new ReceiveRequest { MaxWaitTime = TimeSpan.FromMilliseconds(100) }, visibility, ct));

        // After the lease lapses, node-b reclaims the in-flight message (lease state lives in Redis) and completes it.
        var reclaimedByB = Assert.Single(await nodeB.ReceiveAsync("work", new ReceiveRequest { MaxWaitTime = visibility + TimeSpan.FromSeconds(5) }, visibility, ct));
        Assert.Equal(heldByA.Id, reclaimedByB.Id);
        Assert.Equal(2, reclaimedByB.DeliveryCount);
        Assert.Equal("survive-me", System.Text.Encoding.UTF8.GetString(reclaimedByB.Body.Span));
        await nodeB.CompleteAsync(reclaimedByB, ct);

        var stats = await nodeB.GetStatsAsync("work", ct);
        Assert.Equal(0, stats.Queued);
        Assert.Equal(0, stats.Working);
    }

    [Fact]
    public async Task Core_RetriesFailedHandler_ThenDeadLettersAfterMaxAttemptsAsync()
    {
        if (RedisTestConnection.Multiplexer is not { } connection)
        {
            Assert.Skip("FOUNDATIO_REDIS_CONNECTION_STRING not set.");
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var transport = CreateTransport(connection, NewPrefix());
        await using var queue = new MessageQueue(transport, new QueueOptions());

        // (a) A handler that throws once is redelivered (via the transport) and succeeds on the second attempt — the
        // core's retry machinery works unchanged over Streams.
        int retryAttempts = 0;
        var succeeded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var retryConsumer = await queue.StartConsumerAsync<RetryItem>((message, _) =>
        {
            int attempt = Interlocked.Increment(ref retryAttempts);
            if (attempt == 1)
                throw new InvalidOperationException("first attempt fails");

            Assert.Equal(2, message.Attempts);
            succeeded.TrySetResult();
            return Task.CompletedTask;
        }, new QueueConsumerOptions { MaxAttempts = 3, RedeliveryBackoff = _ => TimeSpan.FromMilliseconds(200) }, ct);

        // (b) A handler that always throws is dead-lettered once its attempt budget is spent.
        await using var poisonConsumer = await queue.StartConsumerAsync<PoisonItem>((_, _) =>
            throw new InvalidOperationException("always fails"),
            new QueueConsumerOptions { MaxAttempts = 2, RedeliveryBackoff = _ => TimeSpan.FromMilliseconds(100) }, ct);

        await queue.EnqueueAsync(new RetryItem { Data = "retry" }, cancellationToken: ct);
        await queue.EnqueueAsync(new PoisonItem { Data = "poison" }, cancellationToken: ct);

        await succeeded.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        Assert.Equal(2, Volatile.Read(ref retryAttempts));

        // The poison message lands in the dead-letter stream after exhausting its 2 attempts.
        MessageDestinationStats stats = await transport.GetStatsAsync("streams-poison", ct);
        for (int i = 0; i < 100 && stats.Deadletter == 0; i++)
        {
            await Task.Delay(100, ct);
            stats = await transport.GetStatsAsync("streams-poison", ct);
        }

        Assert.Equal(1, stats.Deadletter);
        Assert.Equal(0, stats.Working);

        // The poison payload is inspectable in the dead-letter stream with a reason recorded by the core.
        var deadLettered = Assert.Single(await transport.ReceiveDeadLetteredAsync("streams-poison", new ReceiveRequest { MaxMessages = 10 }, ct));
        Assert.NotEmpty(deadLettered.Headers[KnownHeaders.DeadLetterReason]);
    }

    [Fact]
    public async Task PubSub_PublishToTopic_FansOutToEverySubscriptionAsync()
    {
        if (RedisTestConnection.Multiplexer is not { } connection)
        {
            Assert.Skip("FOUNDATIO_REDIS_CONNECTION_STRING not set.");
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var transport = CreateTransport(connection, NewPrefix());
        await using var pubsub = new PubSub(transport, new PubSubOptions());

        var receivedByA = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var receivedByB = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var subA = await pubsub.SubscribeAsync<FanItem>((message, _) =>
        {
            receivedByA.TrySetResult(message.Message.Data ?? "");
            return Task.CompletedTask;
        }, new PubSubSubscriptionOptions { Subscription = "sub-a" }, ct);

        await using var subB = await pubsub.SubscribeAsync<FanItem>((message, _) =>
        {
            receivedByB.TrySetResult(message.Message.Data ?? "");
            return Task.CompletedTask;
        }, new PubSubSubscriptionOptions { Subscription = "sub-b" }, ct);

        await pubsub.PublishAsync(new FanItem { Data = "broadcast" }, cancellationToken: ct);

        // Each named subscription is its own consumer group, so both receive an independent copy. Delivery is
        // poll-driven across two subscriptions (the core pull-fallback loop), so allow generous headroom for the whole
        // conformance suite hammering the same Redis concurrently.
        await Task.WhenAll(receivedByA.Task, receivedByB.Task).WaitAsync(TimeSpan.FromSeconds(30), ct);
        Assert.Equal("broadcast", await receivedByA.Task);
        Assert.Equal("broadcast", await receivedByB.Task);
    }

    private static TransportMessage Message(string body) =>
        new() { Body = System.Text.Encoding.UTF8.GetBytes(body) };

    [MessageRoute("streams-retry")]
    private sealed class RetryItem
    {
        public string? Data { get; set; }
    }

    [MessageRoute("streams-poison")]
    private sealed class PoisonItem
    {
        public string? Data { get; set; }
    }

    [MessageRoute("streams-topic")]
    private sealed class FanItem
    {
        public string? Data { get; set; }
    }
}
