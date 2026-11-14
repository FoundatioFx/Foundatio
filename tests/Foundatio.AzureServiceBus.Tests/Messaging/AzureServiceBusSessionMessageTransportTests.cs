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
}
