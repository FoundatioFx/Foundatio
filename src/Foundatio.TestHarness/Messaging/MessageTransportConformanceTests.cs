using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Foundatio.Queues;
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

    public virtual async Task CanSendAndReceiveBatchAsync()
    {
        var transport = CreateTransport();
        if (transport is not ISupportsPull pull)
            return;

        try
        {
            await EnsureAsync(transport, new DestinationDeclaration { Name = "orders", Role = DestinationRole.Queue });

            var result = await transport.SendAsync("orders", [
                CreateMessage("one", ("tenant", "acme")),
                CreateMessage("two", ("tenant", "acme"))
            ], new TransportSendOptions(), TestCancellationToken);

            Assert.True(result.AllSucceeded);
            Assert.Equal(2, result.Items.Count);
            Assert.All(result.Items, item => Assert.True(item.Success));

            var entries = await pull.ReceiveAsync("orders", new ReceiveRequest
            {
                MaxMessages = 2,
                MaxWaitTime = TimeSpan.FromSeconds(1)
            }, TestCancellationToken);

            Assert.Equal(2, entries.Count);
            Assert.Equal("one", ReadBody(entries[0]));
            Assert.Equal("two", ReadBody(entries[1]));
            Assert.Equal("acme", entries[0].Headers["tenant"]);
            Assert.Equal(1, entries[0].DeliveryCount);

            await transport.CompleteAsync(entries[0], TestCancellationToken);
            await transport.CompleteAsync(entries[1], TestCancellationToken);

            if (transport is ISupportsStats stats)
            {
                QueueStats queueStats = await stats.GetStatsAsync("orders", TestCancellationToken);
                Assert.Equal(0, queueStats.Queued);
                Assert.Equal(0, queueStats.Working);
                Assert.Equal(2, queueStats.Completed);
            }
        }
        finally
        {
            await CleanupTransportIfNotNullAsync(transport);
        }
    }

    public virtual async Task AbandonAsync_RedeliversWithIncrementedDeliveryCountAsync()
    {
        var transport = CreateTransport();
        if (transport is not ISupportsPull pull)
            return;

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

    public virtual async Task CompleteAsync_WithExpiredReceipt_ThrowsReceiptExpiredExceptionAsync()
    {
        var transport = CreateTransport();
        if (transport is not ISupportsPull pull)
            return;

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

    public virtual async Task SubscribeAsync_DeliversPushMessagesAsync()
    {
        var transport = CreateTransport();
        if (transport is not ISupportsPush push)
            return;

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

    public virtual async Task SendAsync_ToTopic_FansOutToSubscriptionsAsync()
    {
        var transport = CreateTransport();
        if (transport is not ISupportsPull pull || transport is not ISupportsProvisioning)
            return;

        try
        {
            await EnsureAsync(transport,
                new DestinationDeclaration { Name = "orders-topic", Role = DestinationRole.Topic },
                new DestinationDeclaration { Name = "orders-subscription-a", Role = DestinationRole.Subscription, Source = "orders-topic" },
                new DestinationDeclaration { Name = "orders-subscription-b", Role = DestinationRole.Subscription, Source = "orders-topic" });

            await transport.SendAsync("orders-topic", [CreateMessage("fanout")], new TransportSendOptions(), TestCancellationToken);

            var first = Assert.Single(await pull.ReceiveAsync("orders-subscription-a", new ReceiveRequest { MaxWaitTime = TimeSpan.FromSeconds(1) }, TestCancellationToken));
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

    public virtual async Task ReceiveAsync_RespectsPriorityAsync()
    {
        var transport = CreateTransport();
        if (transport is not ISupportsPull pull || transport is not ISupportsPriority)
            return;

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

    public virtual async Task SendAsync_WithDeliverAt_DelaysVisibilityAsync()
    {
        var transport = CreateTransport();
        if (transport is not ISupportsPull pull || transport is not ISupportsDelayedDelivery)
            return;

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

    public virtual async Task DeadLetterAsync_MovesEntryToDeadletterStatsAsync()
    {
        var transport = CreateTransport();
        if (transport is not ISupportsPull pull || transport is not ISupportsDeadLetter || transport is not ISupportsStats stats)
            return;

        try
        {
            await EnsureAsync(transport, new DestinationDeclaration { Name = "deadletter", Role = DestinationRole.Queue });
            await transport.SendAsync("deadletter", [CreateMessage("poison")], new TransportSendOptions(), TestCancellationToken);

            var entry = Assert.Single(await pull.ReceiveAsync("deadletter", new ReceiveRequest { MaxWaitTime = TimeSpan.FromSeconds(1) }, TestCancellationToken));
            await ((ISupportsDeadLetter)transport).DeadLetterAsync(entry, "bad-payload", TestCancellationToken);

            QueueStats queueStats = await stats.GetStatsAsync("deadletter", TestCancellationToken);
            Assert.Equal(0, queueStats.Working);
            Assert.Equal(1, queueStats.Deadletter);
        }
        finally
        {
            await CleanupTransportIfNotNullAsync(transport);
        }
    }

    public virtual async Task ReceiveAsync_WithExpiredMessage_DeadlettersAndSkipsAsync()
    {
        var transport = CreateTransport();
        if (transport is not ISupportsPull pull || transport is not ISupportsExpiration || transport is not ISupportsStats stats)
            return;

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

            QueueStats queueStats = await stats.GetStatsAsync("expiration", TestCancellationToken);
            Assert.Equal(0, queueStats.Queued);
            Assert.Equal(1, queueStats.Deadletter);
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
