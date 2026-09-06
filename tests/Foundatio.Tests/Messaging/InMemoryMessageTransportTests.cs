using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Foundatio.Tests.Messaging;

public class InMemoryMessageTransportTests : MessageTransportConformanceTests
{
    public InMemoryMessageTransportTests(ITestOutputHelper output) : base(output) { }

    protected override IMessageTransport CreateTransport()
    {
        return new InMemoryMessageTransport();
    }

    [Fact]
    public async Task CompletedDeliveries_KeepVisibilityTimerResourcesBounded()
    {
        var time = new TrackingTimeProvider();
        await using (var transport = new InMemoryMessageTransport(time))
        {
            var queue = DestinationAddress.ForQueue("timer-resources");
            for (int i = 0; i < 100; i++)
            {
                await transport.SendAsync(queue, [new TransportMessage { Body = new byte[] { 1 } }], new(), TestCancellationToken);
                var entry = Assert.Single(await transport.ReceiveAsync(queue, new(), TestCancellationToken));
                await transport.RenewLockAsync(entry, TimeSpan.FromMinutes(2), TestCancellationToken);
                await transport.CompleteAsync(entry, TestCancellationToken);
            }

            Assert.InRange(time.TimerCount, 0, 2);
            time.Clock.Advance(TimeSpan.FromMinutes(2));
            int callbacks = time.CallbackCount;
            time.Clock.Advance(TimeSpan.FromDays(1));
            Assert.Equal(callbacks, time.CallbackCount);
        }

        Assert.Equal(0, time.TimerCount);
    }

    [Fact]
    public async Task RenewedVisibility_WakesBlockedReceiverOnlyAfterCurrentLeaseExpires()
    {
        var time = new TrackingTimeProvider();
        await using var transport = new InMemoryMessageTransport(time);
        var queue = DestinationAddress.ForQueue("renewed-visibility");
        for (int iteration = 0; iteration < 2; iteration++)
        {
            await transport.SendAsync(queue, [new TransportMessage { Body = new byte[] { 1 } }], new(), TestCancellationToken);
            var entry = Assert.Single(await transport.ReceiveAsync(queue, new(), TimeSpan.FromSeconds(1), TestCancellationToken));
            await transport.RenewLockAsync(entry, TimeSpan.FromSeconds(2), TestCancellationToken);

            var pending = transport.ReceiveAsync(queue, new() { MaxWaitTime = TimeSpan.FromSeconds(10) }, TestCancellationToken);
            time.Clock.Advance(TimeSpan.FromMilliseconds(1200));
            Assert.False(pending.IsCompleted);
            time.Clock.Advance(TimeSpan.FromSeconds(1));
            var redelivered = Assert.Single(await pending.WaitAsync(TimeSpan.FromSeconds(5), TestCancellationToken));
            Assert.Equal(entry.Id, redelivered.Id);
            Assert.Equal(2, redelivered.DeliveryCount);
            await transport.CompleteAsync(redelivered, TestCancellationToken);
            time.Clock.Advance(TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public void DestinationAddress_KeyEncodesTopicAndSubscription()
    {
        var destination = DestinationAddress.ForSubscription("orders", "sub-a");
        Assert.Equal("orders/sub-a", destination.Key);
        Assert.Equal("orders", destination.Topic);
        Assert.Equal("sub-a", destination.Name);
        Assert.Equal(DestinationRole.Subscription, destination.Role);

        // A bare (non-subscription) destination has no topic and a bare key.
        var bare = DestinationAddress.ForQueue("orders");
        Assert.Null(bare.Topic);
        Assert.Equal("orders", bare.Key);
        Assert.NotEqual(destination, bare);
    }

    [Fact]
    public void MessageHeaders_SerializeToJson_RoundTripsCaseInsensitively()
    {
        var headers = MessageHeaders.Create([
            new KeyValuePair<string, string>("Message.Type", "order.created"),
            new KeyValuePair<string, string>("tenant", "acme")
        ]);

        // The shared codec both transports use preserves the case-insensitive contract across the wire.
        var roundTripped = MessageHeaders.DeserializeFromJson(MessageHeaders.SerializeToJson(headers));
        Assert.Equal("order.created", roundTripped["MESSAGE.TYPE"]);
        Assert.Equal("acme", roundTripped["tenant"]);

        Assert.Empty(MessageHeaders.DeserializeFromJson(null));
        Assert.Empty(MessageHeaders.DeserializeFromJson(""));
    }

    [Fact]
    public void MessageHeaders_AreImmutableAndCaseInsensitive()
    {
        var source = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["message.type"] = "order.created"
        };

        var headers = MessageHeaders.Create(source);
        source["message.type"] = "changed";

        Assert.Equal("order.created", headers["MESSAGE.TYPE"]);
        Assert.Equal("order.created", headers.GetValueOrDefault("Message.Type"));
        Assert.True(headers.ContainsKey("MESSAGE.TYPE"));

        var updated = headers.ToBuilder()
            .Set("TraceParent", "00-123")
            .SetIfMissing("traceparent", "ignored")
            .Build();

        Assert.Equal("00-123", updated["traceparent"]);
        Assert.False(headers.ContainsKey("traceparent"));
    }

    private sealed class TrackingTimeProvider : TimeProvider
    {
        private int _timerCount;
        private int _callbackCount;
        public FakeTimeProvider Clock { get; } = new();
        public int TimerCount => Volatile.Read(ref _timerCount);
        public int CallbackCount => Volatile.Read(ref _callbackCount);
        public override DateTimeOffset GetUtcNow() => Clock.GetUtcNow();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Increment(ref _timerCount);
            var timer = Clock.CreateTimer(value => { Interlocked.Increment(ref _callbackCount); callback(value); }, state, dueTime, period);
            return new TrackedTimer(timer, () => Interlocked.Decrement(ref _timerCount));
        }
    }

    private sealed class TrackedTimer(ITimer timer, Action disposed) : ITimer
    {
        private int _disposed;
        public bool Change(TimeSpan dueTime, TimeSpan period) => timer.Change(dueTime, period);
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            timer.Dispose();
            disposed();
        }
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
