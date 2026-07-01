using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Foundatio.Xunit;
using Xunit;

namespace Foundatio.Tests.Messaging;

public abstract class MessageTransportConformanceTests : TestWithLoggingBase
{
    protected MessageTransportConformanceTests(ITestOutputHelper output) : base(output) { }

    protected virtual IMessageTransport? CreateTransport()
    {
        return null;
    }

    protected virtual ValueTask CleanupTransportAsync(IMessageTransport transport)
    {
        return transport.DisposeAsync();
    }

    [Fact]
    public virtual async Task CanSendAndReceiveBatchAsync()
    {
        var transport = CreateTransport();
        if (transport is not ISupportsPull pull)
        {
            Assert.Skip("Transport does not support pull receive (ISupportsPull).");
            return;
        }

        try
        {
            await EnsureAsync(transport, new DestinationDeclaration { Name = "orders", Role = DestinationRole.Queue });

            var result = await transport.SendAsync("orders", [
                CreateMessage("one", ("tenant", "acme")),
                CreateMessage("two", ("tenant", "acme"))
            ], new TransportSendOptions(), TestCancellationToken);

            // Send is throw-on-failure, so reaching here means both messages were accepted; assert the accepted ids.
            Assert.Equal(2, result.Items.Count);

            var entries = await pull.ReceiveAsync("orders", new ReceiveRequest
            {
                MaxMessages = 2,
                MaxWaitTime = TimeSpan.FromSeconds(1)
            }, TestCancellationToken);

            Assert.Equal(2, entries.Count);
            var bodies = entries.Select(ReadBody).ToList();
            Assert.Contains("one", bodies);
            Assert.Contains("two", bodies);

            // Only assert positional FIFO order when the transport actually guarantees ordering; a best-effort
            // (OrderingGuarantee.None) transport may legitimately deliver out of order.
            if (transport is not ITransportInfo { Ordering: OrderingGuarantee.None })
            {
                Assert.Equal("one", ReadBody(entries[0]));
                Assert.Equal("two", ReadBody(entries[1]));
            }

            Assert.All(entries, e => Assert.Equal("acme", e.Headers["tenant"]));
            Assert.Equal(1, entries[0].DeliveryCount);

            await transport.CompleteAsync(entries[0], TestCancellationToken);
            await transport.CompleteAsync(entries[1], TestCancellationToken);

            if (transport is ISupportsStats stats)
            {
                // Assert only the point-in-time gauges every broker can report, and tolerate eventual consistency
                // (e.g. SQS ApproximateNumberOf* lag). Lifetime counters such as Completed are not universally
                // available across transports, so they are not part of the shared contract.
                await AssertQueueDrainedAsync(stats, "orders", TestCancellationToken);
            }
        }
        finally
        {
            await CleanupTransportIfNotNullAsync(transport);
        }
    }

    [Fact]
    public virtual async Task AbandonAsync_RedeliversWithIncrementedDeliveryCountAsync()
    {
        var transport = CreateTransport();
        if (transport is not ISupportsPull pull)
        {
            Assert.Skip("Transport does not support pull receive (ISupportsPull).");
            return;
        }

        try
        {
            await EnsureAsync(transport, new DestinationDeclaration { Name = "retry", Role = DestinationRole.Queue });
            await transport.SendAsync("retry", [CreateMessage("retry-me")], new TransportSendOptions(), TestCancellationToken);

            var first = Assert.Single(await pull.ReceiveAsync("retry", new ReceiveRequest { MaxWaitTime = TimeSpan.FromSeconds(1) }, TestCancellationToken));
            Assert.Equal(1, first.DeliveryCount);

            await transport.AbandonAsync(first, TestCancellationToken);

            var second = Assert.Single(await pull.ReceiveAsync("retry", new ReceiveRequest { MaxWaitTime = TimeSpan.FromSeconds(1) }, TestCancellationToken));
            Assert.Equal(first.Id, second.Id);
            Assert.Equal(2, second.DeliveryCount);
            Assert.Equal("retry-me", ReadBody(second));

            await transport.CompleteAsync(second, TestCancellationToken);
        }
        finally
        {
            await CleanupTransportIfNotNullAsync(transport);
        }
    }

    [Fact]
    public virtual async Task CompleteAsync_WithExpiredReceipt_ThrowsReceiptExpiredExceptionAsync()
    {
        var transport = CreateTransport();
        if (transport is not ISupportsPull pull)
        {
            Assert.Skip("Transport does not support pull receive (ISupportsPull).");
            return;
        }

        try
        {
            await EnsureAsync(transport, new DestinationDeclaration { Name = "receipts", Role = DestinationRole.Queue });
            await transport.SendAsync("receipts", [CreateMessage("done")], new TransportSendOptions(), TestCancellationToken);

            var entry = Assert.Single(await pull.ReceiveAsync("receipts", new ReceiveRequest { MaxWaitTime = TimeSpan.FromSeconds(1) }, TestCancellationToken));
            await transport.CompleteAsync(entry, TestCancellationToken);

            await Assert.ThrowsAsync<ReceiptExpiredException>(async () =>
                await transport.CompleteAsync(entry, TestCancellationToken));
        }
        finally
        {
            await CleanupTransportIfNotNullAsync(transport);
        }
    }

    [Fact]
    public virtual async Task SubscribeAsync_DeliversPushMessagesAsync()
    {
        var transport = CreateTransport();
        if (transport is not ISupportsPush push)
        {
            Assert.Skip("Transport does not support push delivery (ISupportsPush).");
            return;
        }

        try
        {
            await EnsureAsync(transport, new DestinationDeclaration { Name = "push", Role = DestinationRole.Queue });

            var received = new TaskCompletionSource<TransportEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var subscription = await push.SubscribeAsync("push", async (entry, ct) =>
            {
                await transport.CompleteAsync(entry, ct);
                received.TrySetResult(entry);
            }, new PushOptions(), TestCancellationToken);

            await transport.SendAsync("push", [CreateMessage("pushed")], new TransportSendOptions(), TestCancellationToken);

            var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(3), TestCancellationToken));
            Assert.Equal(received.Task, completed);
            Assert.Equal("pushed", ReadBody(await received.Task));
            Assert.Equal("push", subscription.Source);
        }
        finally
        {
            await CleanupTransportIfNotNullAsync(transport);
        }
    }

    [Fact]
    public virtual async Task SendAsync_ToTopic_FansOutToSubscriptionsAsync()
    {
        var transport = CreateTransport();
        if (transport is not ISupportsPull pull || transport is not ISupportsProvisioning)
        {
            Assert.Skip("Transport does not support pull receive and provisioning (ISupportsPull + ISupportsProvisioning).");
            return;
        }

        try
        {
            await EnsureAsync(transport,
                new DestinationDeclaration { Name = "orders-topic", Role = DestinationRole.Topic },
                new DestinationDeclaration { Name = "orders-subscription-a", Role = DestinationRole.Subscription, Source = "orders-topic" },
                new DestinationDeclaration { Name = "orders-subscription-b", Role = DestinationRole.Subscription, Source = "orders-topic" });

            // The caller states the destination role; publishing to a topic must set DestinationRole.Topic.
            await transport.SendAsync("orders-topic", [CreateMessage("fanout")], new TransportSendOptions { DestinationRole = DestinationRole.Topic }, TestCancellationToken);

            var first = Assert.Single(await pull.ReceiveAsync("orders-subscription-a", new ReceiveRequest { MaxWaitTime = TimeSpan.FromSeconds(2) }, TestCancellationToken));
            var second = Assert.Single(await pull.ReceiveAsync("orders-subscription-b", new ReceiveRequest { MaxWaitTime = TimeSpan.FromSeconds(1) }, TestCancellationToken));

            Assert.Equal("fanout", ReadBody(first));
            Assert.Equal("fanout", ReadBody(second));

            await transport.CompleteAsync(first, TestCancellationToken);
            await transport.CompleteAsync(second, TestCancellationToken);
        }
        finally
        {
            await CleanupTransportIfNotNullAsync(transport);
        }
    }

    [Fact]
    public virtual async Task ReceiveAsync_RespectsPriorityAsync()
    {
        var transport = CreateTransport();
        if (transport is not ISupportsPull pull || transport is not ISupportsPriority)
        {
            Assert.Skip("Transport does not support pull receive with priority (ISupportsPull + ISupportsPriority).");
            return;
        }

        try
        {
            await EnsureAsync(transport, new DestinationDeclaration { Name = "priority", Role = DestinationRole.Queue });
            await transport.SendAsync("priority", [CreateMessage("low")], new TransportSendOptions { Priority = MessagePriority.Low }, TestCancellationToken);
            await transport.SendAsync("priority", [CreateMessage("high")], new TransportSendOptions { Priority = MessagePriority.High }, TestCancellationToken);
            await transport.SendAsync("priority", [CreateMessage("normal")], new TransportSendOptions { Priority = MessagePriority.Normal }, TestCancellationToken);

            var entries = await pull.ReceiveAsync("priority", new ReceiveRequest
            {
                MaxMessages = 3,
                MaxWaitTime = TimeSpan.FromSeconds(1)
            }, TestCancellationToken);

            Assert.Equal(3, entries.Count);
            Assert.Equal("high", ReadBody(entries[0]));
            Assert.Equal("normal", ReadBody(entries[1]));
            Assert.Equal("low", ReadBody(entries[2]));

            foreach (var entry in entries)
                await transport.CompleteAsync(entry, TestCancellationToken);
        }
        finally
        {
            await CleanupTransportIfNotNullAsync(transport);
        }
    }

    [Fact]
    public virtual async Task SendAsync_WithDeliverAt_DelaysVisibilityAsync()
    {
        var transport = CreateTransport();
        if (transport is not ISupportsPull pull || transport is not ISupportsDelayedDelivery)
        {
            Assert.Skip("Transport does not support pull receive with delayed delivery (ISupportsPull + ISupportsDelayedDelivery).");
            return;
        }

        try
        {
            await EnsureAsync(transport, new DestinationDeclaration { Name = "delayed", Role = DestinationRole.Queue });
            await transport.SendAsync("delayed", [CreateMessage("later")], new TransportSendOptions
            {
                DeliverAt = DateTimeOffset.UtcNow.AddMilliseconds(250)
            }, TestCancellationToken);

            var immediate = await pull.ReceiveAsync("delayed", new ReceiveRequest { MaxWaitTime = TimeSpan.FromMilliseconds(50) }, TestCancellationToken);
            Assert.Empty(immediate);

            var delayed = Assert.Single(await pull.ReceiveAsync("delayed", new ReceiveRequest { MaxWaitTime = TimeSpan.FromSeconds(2) }, TestCancellationToken));
            Assert.Equal("later", ReadBody(delayed));
            await transport.CompleteAsync(delayed, TestCancellationToken);
        }
        finally
        {
            await CleanupTransportIfNotNullAsync(transport);
        }
    }

    [Fact]
    public virtual async Task DeadLetterAsync_MovesEntryToDeadletterStatsAsync()
    {
        var transport = CreateTransport();
        if (transport is not ISupportsPull pull || transport is not ISupportsDeadLetter || transport is not ISupportsStats stats)
        {
            Assert.Skip("Transport does not support pull receive with dead-letter and stats (ISupportsPull + ISupportsDeadLetter + ISupportsStats).");
            return;
        }

        try
        {
            await EnsureAsync(transport, new DestinationDeclaration { Name = "deadletter", Role = DestinationRole.Queue });
            await transport.SendAsync("deadletter", [CreateMessage("poison")], new TransportSendOptions(), TestCancellationToken);

            var entry = Assert.Single(await pull.ReceiveAsync("deadletter", new ReceiveRequest { MaxWaitTime = TimeSpan.FromSeconds(1) }, TestCancellationToken));
            await ((ISupportsDeadLetter)transport).DeadLetterAsync(entry, "bad-payload", TestCancellationToken);

            MessageDestinationStats queueStats = await stats.GetStatsAsync("deadletter", TestCancellationToken);
            Assert.Equal(0, queueStats.Working);
            Assert.Equal(1, queueStats.Deadletter);
        }
        finally
        {
            await CleanupTransportIfNotNullAsync(transport);
        }
    }

    [Fact]
    public virtual async Task ReceiveAsync_WithExpiredMessage_DeadlettersAndSkipsAsync()
    {
        var transport = CreateTransport();
        if (transport is not ISupportsPull pull || transport is not ISupportsExpiration || transport is not ISupportsStats stats)
        {
            Assert.Skip("Transport does not support pull receive with expiration and stats (ISupportsPull + ISupportsExpiration + ISupportsStats).");
            return;
        }

        try
        {
            await EnsureAsync(transport, new DestinationDeclaration { Name = "expiration", Role = DestinationRole.Queue });
            var expired = new TransportMessage
            {
                Body = Encoding.UTF8.GetBytes("expired"),
                Headers = MessageHeaders.Create([
                    new KeyValuePair<string, string>(KnownHeaders.Expiration, DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"))
                ])
            };

            await transport.SendAsync("expiration", [expired], new TransportSendOptions(), TestCancellationToken);

            var entries = await pull.ReceiveAsync("expiration", new ReceiveRequest { MaxWaitTime = TimeSpan.FromMilliseconds(50) }, TestCancellationToken);
            Assert.Empty(entries);

            MessageDestinationStats queueStats = await stats.GetStatsAsync("expiration", TestCancellationToken);
            Assert.Equal(0, queueStats.Queued);
            Assert.Equal(1, queueStats.Deadletter);
        }
        finally
        {
            await CleanupTransportIfNotNullAsync(transport);
        }
    }
    [Fact]
    public virtual async Task ReceiveAsync_AfterVisibilityTimeout_RedeliversAsync()
    {
        var transport = CreateTransport();
        if (transport is not ISupportsVisibilityTimeout visibility)
        {
            Assert.Skip("Transport does not support visibility timeout (ISupportsVisibilityTimeout).");
            return;
        }

        try
        {
            await EnsureAsync(transport, new DestinationDeclaration { Name = "visibility", Role = DestinationRole.Queue });
            await transport.SendAsync("visibility", [CreateMessage("lease")], new TransportSendOptions(), TestCancellationToken);

            // Whole-second visibility window: real brokers (e.g. SQS) only support second-resolution visibility timeouts.
            var visibilityWindow = TimeSpan.FromSeconds(2);
            var first = Assert.Single(await visibility.ReceiveAsync("visibility", new ReceiveRequest { MaxWaitTime = TimeSpan.FromSeconds(2) }, visibilityWindow, TestCancellationToken));
            Assert.Equal(1, first.DeliveryCount);

            // Still within the visibility window: a competing receive must not see the in-flight message.
            var hidden = await visibility.ReceiveAsync("visibility", new ReceiveRequest { MaxWaitTime = TimeSpan.FromMilliseconds(100) }, visibilityWindow, TestCancellationToken);
            Assert.Empty(hidden);

            // After the visibility window lapses without settlement the message must be redelivered (at-least-once). A
            // long poll observes the lapse — a transport wakes a blocked receive when a visibility window expires — so
            // this is robust to coarse/variable redelivery latency without a fixed sleep.
            var second = Assert.Single(await visibility.ReceiveAsync("visibility", new ReceiveRequest { MaxWaitTime = visibilityWindow + TimeSpan.FromSeconds(5) }, visibilityWindow, TestCancellationToken));
            Assert.Equal(first.Id, second.Id);
            Assert.Equal(2, second.DeliveryCount);

            await transport.CompleteAsync(second, TestCancellationToken);
        }
        finally
        {
            await CleanupTransportIfNotNullAsync(transport);
        }
    }

    [Fact]
    public virtual async Task AbandonAsync_WithRedeliveryDelay_RedeliversAfterDelayAsync()
    {
        var transport = CreateTransport();
        if (transport is not ISupportsPull pull || transport is not ISupportsRedeliveryDelay redelivery)
        {
            Assert.Skip("Transport does not support pull receive with redelivery delay (ISupportsPull + ISupportsRedeliveryDelay).");
            return;
        }

        try
        {
            await EnsureAsync(transport, new DestinationDeclaration { Name = "redelivery-delay", Role = DestinationRole.Queue });
            await transport.SendAsync("redelivery-delay", [CreateMessage("delay-me")], new TransportSendOptions(), TestCancellationToken);

            var first = Assert.Single(await pull.ReceiveAsync("redelivery-delay", new ReceiveRequest { MaxWaitTime = TimeSpan.FromSeconds(2) }, TestCancellationToken));
            Assert.Equal(1, first.DeliveryCount);

            // Whole-second redelivery delay: SQS serves this via ChangeMessageVisibility, which is second-resolution.
            var redeliveryDelay = TimeSpan.FromSeconds(2);
            await redelivery.AbandonAsync(first, redeliveryDelay, TestCancellationToken);

            // Within the delay window the message must not be visible again.
            var early = await pull.ReceiveAsync("redelivery-delay", new ReceiveRequest { MaxWaitTime = TimeSpan.FromMilliseconds(100) }, TestCancellationToken);
            Assert.Empty(early);

            // After the delay lapses it is redelivered with an incremented delivery count. Long poll for robustness.
            var second = Assert.Single(await pull.ReceiveAsync("redelivery-delay", new ReceiveRequest { MaxWaitTime = redeliveryDelay + TimeSpan.FromSeconds(5) }, TestCancellationToken));
            Assert.Equal(first.Id, second.Id);
            Assert.Equal(2, second.DeliveryCount);
            Assert.Equal("delay-me", ReadBody(second));

            await transport.CompleteAsync(second, TestCancellationToken);
        }
        finally
        {
            await CleanupTransportIfNotNullAsync(transport);
        }
    }

    [Fact]
    public virtual async Task RenewLockAsync_ExtendsVisibilityWindowAsync()
    {
        var transport = CreateTransport();
        if (transport is not ISupportsVisibilityTimeout visibility || transport is not ISupportsLockRenewal lockRenewal)
        {
            Assert.Skip("Transport does not support visibility timeout with lock renewal (ISupportsVisibilityTimeout + ISupportsLockRenewal).");
            return;
        }

        try
        {
            await EnsureAsync(transport, new DestinationDeclaration { Name = "lock-renewal", Role = DestinationRole.Queue });
            await transport.SendAsync("lock-renewal", [CreateMessage("hold")], new TransportSendOptions(), TestCancellationToken);

            // Whole-second windows so the test maps onto second-resolution brokers (e.g. SQS).
            var originalWindow = TimeSpan.FromSeconds(2);
            var renewedWindow = TimeSpan.FromSeconds(8);
            var first = Assert.Single(await visibility.ReceiveAsync("lock-renewal", new ReceiveRequest { MaxWaitTime = TimeSpan.FromSeconds(2) }, originalWindow, TestCancellationToken));
            Assert.Equal(1, first.DeliveryCount);

            // Renew before the original window lapses, extending it well past the original expiry.
            await Task.Delay(TimeSpan.FromSeconds(1), TestCancellationToken);
            await lockRenewal.RenewLockAsync(first, renewedWindow, TestCancellationToken);

            // Past the original window but inside the renewed window: the message must still be held, so a competing
            // receive sees nothing rather than a premature redelivery.
            await Task.Delay(originalWindow, TestCancellationToken);
            var held = await visibility.ReceiveAsync("lock-renewal", new ReceiveRequest { MaxWaitTime = TimeSpan.FromMilliseconds(100) }, originalWindow, TestCancellationToken);
            Assert.Empty(held);

            await transport.CompleteAsync(first, TestCancellationToken);
        }
        finally
        {
            await CleanupTransportIfNotNullAsync(transport);
        }
    }

    [Fact]
    public virtual async Task CompetingConsumers_DoNotReceiveTheSameInFlightMessageAsync()
    {
        var transport = CreateTransport();
        if (transport is not ISupportsPull pull)
        {
            Assert.Skip("Transport does not support pull receive (ISupportsPull).");
            return;
        }

        try
        {
            await EnsureAsync(transport, new DestinationDeclaration { Name = "competing", Role = DestinationRole.Queue });
            await transport.SendAsync("competing", [CreateMessage("once")], new TransportSendOptions(), TestCancellationToken);

            var first = Assert.Single(await pull.ReceiveAsync("competing", new ReceiveRequest { MaxWaitTime = TimeSpan.FromSeconds(1) }, TestCancellationToken));

            // A competing consumer must not receive the same message while it is in flight.
            var second = await pull.ReceiveAsync("competing", new ReceiveRequest { MaxWaitTime = TimeSpan.FromMilliseconds(100) }, TestCancellationToken);
            Assert.Empty(second);

            await transport.CompleteAsync(first, TestCancellationToken);
        }
        finally
        {
            await CleanupTransportIfNotNullAsync(transport);
        }
    }

    [Fact]
    public virtual async Task ReceiveDeadLetteredAsync_ReturnsPoisonPayloadAndReasonAsync()
    {
        var transport = CreateTransport();
        if (transport is not ISupportsPull pull || transport is not ISupportsDeadLetter deadLetter)
        {
            Assert.Skip("Transport does not support pull receive and dead-letter (ISupportsPull + ISupportsDeadLetter).");
            return;
        }

        try
        {
            await EnsureAsync(transport, new DestinationDeclaration { Name = "dlq-read", Role = DestinationRole.Queue });
            await transport.SendAsync("dlq-read", [CreateMessage("poison", ("tenant", "acme"))], new TransportSendOptions(), TestCancellationToken);

            var entry = Assert.Single(await pull.ReceiveAsync("dlq-read", new ReceiveRequest { MaxWaitTime = TimeSpan.FromSeconds(1) }, TestCancellationToken));
            await deadLetter.DeadLetterAsync(entry, "bad-payload", TestCancellationToken);

            // The raw (un-deserialized) payload and the dead-letter reason must be inspectable.
            var deadLettered = Assert.Single(await deadLetter.ReceiveDeadLetteredAsync("dlq-read", new ReceiveRequest { MaxMessages = 10 }, TestCancellationToken));
            Assert.Equal("poison", ReadBody(deadLettered));
            Assert.Equal("acme", deadLettered.Headers["tenant"]);
            Assert.Equal("bad-payload", deadLettered.Headers[KnownHeaders.DeadLetterReason]);
        }
        finally
        {
            await CleanupTransportIfNotNullAsync(transport);
        }
    }

    [Fact]
    public virtual async Task SendAsync_PreservesBinaryBodyAndCaseInsensitiveHeadersAsync()
    {
        var transport = CreateTransport();
        if (transport is not ISupportsPull pull)
        {
            Assert.Skip("Transport does not support pull receive (ISupportsPull).");
            return;
        }

        try
        {
            await EnsureAsync(transport, new DestinationDeclaration { Name = "binary", Role = DestinationRole.Queue });

            // Arbitrary, non-UTF-8 bytes with no content type must round-trip exactly (catches body-encoding bugs — a
            // provider must not assume text), and header keys must round-trip case-insensitively across the wire.
            byte[] payload = [0x00, 0x01, 0xFF, 0xFE, 0x10, 0x80, 0x7F];
            await transport.SendAsync("binary", [new TransportMessage
            {
                Body = payload,
                Headers = MessageHeaders.Create([
                    new KeyValuePair<string, string>("tenant", "acme"),
                    new KeyValuePair<string, string>("Mixed.Case", "x")
                ])
            }], new TransportSendOptions(), TestCancellationToken);

            var entry = Assert.Single(await pull.ReceiveAsync("binary", new ReceiveRequest { MaxWaitTime = TimeSpan.FromSeconds(2) }, TestCancellationToken));
            Assert.Equal(payload, entry.Body.ToArray());
            Assert.Equal("acme", entry.Headers["tenant"]);
            Assert.Equal("x", entry.Headers["MIXED.CASE"]);

            await transport.CompleteAsync(entry, TestCancellationToken);
        }
        finally
        {
            await CleanupTransportIfNotNullAsync(transport);
        }
    }

    private async ValueTask CleanupTransportIfNotNullAsync(IMessageTransport? transport)
    {
        if (transport is not null)
            await CleanupTransportAsync(transport);
    }

    // Polls until the destination reports no queued or in-flight messages (the point-in-time gauges every broker can
    // report), tolerating transports whose stats are only eventually consistent (e.g. SQS ApproximateNumberOf*).
    private async Task AssertQueueDrainedAsync(ISupportsStats stats, string destination, CancellationToken cancellationToken)
    {
        var current = await stats.GetStatsAsync(destination, cancellationToken);
        for (int attempt = 0; attempt < 50 && (current.Queued != 0 || current.Working != 0); attempt++)
        {
            await Task.Delay(100, cancellationToken);
            current = await stats.GetStatsAsync(destination, cancellationToken);
        }

        Assert.Equal(0, current.Queued);
        Assert.Equal(0, current.Working);
    }

    private static async Task EnsureAsync(IMessageTransport transport, params DestinationDeclaration[] declarations)
    {
        if (transport is ISupportsProvisioning provisioning)
            await provisioning.EnsureAsync(declarations, CancellationToken.None);
    }

    private static TransportMessage CreateMessage(string body, params (string Key, string Value)[] headers)
    {
        return new TransportMessage
        {
            Body = Encoding.UTF8.GetBytes(body),
            Headers = MessageHeaders.Create(ToKeyValuePairs(headers))
        };
    }

    private static IEnumerable<KeyValuePair<string, string>> ToKeyValuePairs((string Key, string Value)[] headers)
    {
        foreach (var header in headers)
            yield return new KeyValuePair<string, string>(header.Key, header.Value);
    }

    private static string ReadBody(TransportEntry entry)
    {
        return Encoding.UTF8.GetString(entry.Body.Span);
    }
}
