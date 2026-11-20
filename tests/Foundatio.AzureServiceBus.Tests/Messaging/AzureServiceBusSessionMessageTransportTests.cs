using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Foundatio.Messaging.Transport;
using Xunit;

namespace Foundatio.AzureServiceBus.Tests.Messaging;

/// <summary>Runs the conformance suite and the Service Bus tests against session-enabled queues and subscriptions, plus what only sessions do.</summary>
public class AzureServiceBusSessionMessageTransportTests(ITestOutputHelper output) : AzureServiceBusMessageTransportTests(output)
{
    protected override bool RequiresSession => true;

    [Fact]
    public async Task Sessions_GroupKeys_AreSessionIdsAndUngroupedMessagesShareAFewSessions()
    {
        // Arrange
        var source = MessageSource.Queue(NewName("session-ids"));
        await using var receiver = await CreateReceiverAsync(source);

        // Act
        await Transport.SendAsync(MessageDestination.Queue(source.Name),
            [Message("grouped", "id-grouped") with { GroupKey = "order-42" }, .. Enumerable.Range(0, 30).Select(i => Message($"m{i}", $"id-{i}"))], TestCancellationToken);

        // Assert
        var messages = new List<AzureServiceBusIncomingMessage>();
        while (messages.Count < 31)
        {
            foreach (var message in (await ReceiveAsync(receiver, 1)).Cast<AzureServiceBusIncomingMessage>())
            {
                messages.Add(message);
                await receiver.CompleteAsync(message, TestCancellationToken);
            }
        }

        var grouped = Assert.Single(messages, m => m.MessageId == "id-grouped");
        Assert.Equal("order-42", grouped.Received.SessionId);
        Assert.Equal("order-42", grouped.GroupKey);
        var ungrouped = messages.Where(m => m.MessageId != "id-grouped").ToList();
        Assert.All(ungrouped, m => Assert.Null(m.GroupKey));
        Assert.All(ungrouped, m => Assert.StartsWith(AzureServiceBusMessageTransportCodec.UngroupedSessionPrefix, m.Received.SessionId));
        Assert.InRange(ungrouped.Select(m => m.Received.SessionId).Distinct().Count(), 1, AzureServiceBusMessageTransportCodec.UngroupedSessionCount);
    }

    [Fact]
    public async Task Sessions_UngroupedMessages_AreNotHeldBehindEachOther()
    {
        // Arrange
        var source = MessageSource.Queue(NewName("session-ungrouped"));
        await using var receiver = await CreateReceiverAsync(source);
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [.. Enumerable.Range(0, 10).Select(i => Message($"m{i}", $"id-{i}"))], TestCancellationToken);

        // Act: receive them one at a time, settling none.
        var messages = await ReceiveAsync(receiver, 10);

        // Assert
        Assert.Equal(10, messages.Select(m => m.MessageId).Distinct().Count());
    }

    [Fact]
    public async Task Sessions_DelayedGroupedMessageHeldByADisposedReceiver_IsDeliveredWhenDueAsTheFirstAttempt()
    {
        // Arrange
        var source = MessageSource.Queue(NewName("session-held"));
        var delay = TimeSpan.FromSeconds(4);
        var stopwatch = Stopwatch.StartNew();
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [
            Message("first", "a-0") with { GroupKey = "a", DeliverAt = DateTimeOffset.UtcNow.Add(delay) },
            Message("second", "a-1") with { GroupKey = "a" }
        ], TestCancellationToken);

        // Act: the first receiver holds the session, and the delayed message, until it is disposed.
        var first = await CreateReceiverAsync(source);
        await AssertNothingReceivedAsync(first, TimeSpan.FromSeconds(1));
        await first.DisposeAsync();
        await using var second = await CreatePeerReceiverAsync(source);
        var messages = await ReceiveAsync(second, 2, delay + DeliveryTimeout);

        // Assert
        Assert.True(stopwatch.Elapsed >= delay * 0.75, $"Delivered after {stopwatch.Elapsed}, before it was due.");
        Assert.Equal(["a-0", "a-1"], messages.Select(m => m.MessageId));
        Assert.Equal(1, messages[0].DeliveryCount);
    }

    [Fact]
    public async Task DisposeAsync_WhileAGroupedRetryWaitsOutItsDelay_KeepsTheDelayForTheNextReceiver()
    {
        // Arrange
        var source = MessageSource.Queue(NewName("session-retry-held"));
        var delay = TimeSpan.FromSeconds(4);
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [
            Message("first", "a-0") with { GroupKey = "a" },
            Message("second", "a-1") with { GroupKey = "a" }
        ], TestCancellationToken);
        var first = await CreateReceiverAsync(source);
        var held = await ReceiveOneAsync(first);
        Assert.Equal("a-0", held.MessageId);
        var stopwatch = Stopwatch.StartNew();
        await first.AbandonAsync(held, delay, TestCancellationToken);

        // Act: the receiver holding the retry, and its session, goes away before the retry is due.
        await first.DisposeAsync();
        await using var second = await CreatePeerReceiverAsync(source);
        var messages = await ReceiveAsync(second, 2, delay + DeliveryTimeout);

        // Assert
        Assert.True(stopwatch.Elapsed >= delay * 0.75, $"The retry was delivered after {stopwatch.Elapsed}, before its delay of {delay} had passed.");
        Assert.Equal(["a-0", "a-1"], messages.Select(m => m.MessageId));
        Assert.Equal(held.DeliveryCount + 1, messages[0].DeliveryCount);
    }

    [Fact]
    public async Task ReceiveAsync_WhileTheOnlyActiveGroupIsInFlight_ReturnsItsNextMessageSoonAfterItIsSettled()
    {
        // Arrange
        var source = MessageSource.Queue(NewName("session-hot"));
        await using var receiver = await CreateReceiverAsync(source);
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [.. Enumerable.Range(0, 5).Select(i => Message($"a{i}", $"a-{i}") with { GroupKey = "a" })], TestCancellationToken);
        var current = await ReceiveOneAsync(receiver);
        var stopwatch = Stopwatch.StartNew();

        // Act: each receive starts while the group's previous message is still being handled, as it does under a
        // processor whose other slots are free.
        var order = new List<string> { current.MessageId };
        for (int i = 1; i < 5; i++)
        {
            var next = receiver.ReceiveAsync(1, TimeSpan.FromSeconds(20), TestCancellationToken);
            await receiver.CompleteAsync(current, TestCancellationToken);
            current = Assert.Single(await next);
            order.Add(current.MessageId);
        }

        await receiver.CompleteAsync(current, TestCancellationToken);

        // Assert
        Assert.Equal(["a-0", "a-1", "a-2", "a-3", "a-4"], order);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Receiving the group one message at a time took {stopwatch.Elapsed}.");
    }
}
