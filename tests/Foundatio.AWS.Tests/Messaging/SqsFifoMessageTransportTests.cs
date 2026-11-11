using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Amazon.SQS;
using Amazon.SQS.Model;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;
using Xunit;

namespace Foundatio.AWS.Tests.Messaging;

/// <summary>Runs the conformance suite and the SQS tests against FIFO queues and topics, plus what only FIFO does.</summary>
public class SqsFifoMessageTransportTests(ITestOutputHelper output) : SqsMessageTransportTests(output)
{
    protected override bool Fifo => true;

    [Fact]
    public async Task Fifo_QueuesTopicsAndDeadLetterQueues_AreFifo()
    {
        // Arrange
        string name = NewName("fifo-kinds");
        var queue = MessageSource.Queue(name);
        await using var queueReceiver = await CreateReceiverAsync(queue);
        await using var subscriptionReceiver = await CreateReceiverAsync(MessageSource.Subscription(name, "sub"));

        // Act
        await Transport.SendAsync(MessageDestination.Queue(name), [Message("poison")], TestCancellationToken);
        await queueReceiver.DeadLetterAsync(await ReceiveOneAsync(queueReceiver), "fifo", TestCancellationToken);

        // Assert
        using var sqs = CreateSqsClient();
        using var sns = CreateSnsClient();
        var queueUrls = (await sqs.ListQueuesAsync(new ListQueuesRequest { QueueNamePrefix = NamePrefix + name }, TestCancellationToken)).QueueUrls ?? [];
        Assert.Equal(3, queueUrls.Count);
        foreach (string queueUrl in queueUrls)
        {
            Assert.EndsWith(".fifo", queueUrl);
            var attributes = await sqs.GetQueueAttributesAsync(queueUrl, [QueueAttributeName.FifoQueue, QueueAttributeName.MessageRetentionPeriod], TestCancellationToken);
            Assert.Equal("true", attributes.Attributes[QueueAttributeName.FifoQueue]);
            Assert.Equal("1209600", attributes.Attributes[QueueAttributeName.MessageRetentionPeriod]);
        }

        var topic = await sns.GetTopicAttributesAsync(await GetTopicArnAsync(sns, name), TestCancellationToken);
        Assert.Equal("true", topic.Attributes["FifoTopic"]);
    }

    [Fact]
    public async Task Fifo_UngroupedMessages_AreNotHeldBehindEachOther()
    {
        // Arrange
        var source = MessageSource.Queue(NewName("fifo-ungrouped"));
        await using var receiver = await CreateReceiverAsync(source);
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("one", "id-1"), Message("two", "id-2")], TestCancellationToken);

        // Act
        var first = Assert.Single(await receiver.ReceiveAsync(1, DeliveryTimeout, TestCancellationToken));
        var second = await ReceiveOneAsync(receiver);

        // Assert
        Assert.NotEqual(first.MessageId, second.MessageId);
    }

    [Fact]
    public async Task Fifo_RepeatedMessageIdWithoutADeduplicationId_IsNotDropped()
    {
        // Arrange
        var source = MessageSource.Queue(NewName("fifo-same-id"));
        await using var receiver = await CreateReceiverAsync(source);

        // Act
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("first", "same-id")], TestCancellationToken);
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("again", "same-id")], TestCancellationToken);

        // Assert
        var messages = await ReceiveAsync(receiver, 2);
        Assert.All(messages, m => Assert.Equal("same-id", m.MessageId));
    }

    [Fact]
    public async Task Fifo_DelayedMessage_HoldsBackLaterMessagesOfItsGroupOnly()
    {
        // Arrange
        var source = MessageSource.Queue(NewName("fifo-delay-group"));
        await using var receiver = await CreateReceiverAsync(source);
        var stopwatch = Stopwatch.StartNew();

        // Act
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [
            Message("first", "g-0") with { GroupKey = "g", DeliverAt = DateTimeOffset.UtcNow.Add(ShortDelay) },
            Message("second", "g-1") with { GroupKey = "g" },
            Message("other", "o-0") with { GroupKey = "o" }
        ], TestCancellationToken);

        var received = new List<(IncomingMessage Message, TimeSpan At)>();
        var deadline = DateTime.UtcNow + ShortDelay + DeliveryTimeout;
        while (received.Count < 3 && DateTime.UtcNow < deadline)
        {
            foreach (var message in await receiver.ReceiveAsync(10, TimeSpan.FromSeconds(1), TestCancellationToken))
            {
                received.Add((message, stopwatch.Elapsed));
                await receiver.CompleteAsync(message, TestCancellationToken);
            }
        }

        // Assert
        Assert.Equal(["g-0", "g-1"], received.Where(r => r.Message.GroupKey == "g").Select(r => r.Message.MessageId));
        Assert.All(received.Where(r => r.Message.GroupKey == "g"), r => Assert.True(r.At >= ShortDelay * 0.75, $"{r.Message.MessageId} was delivered after {r.At}, before its group's delayed message was due."));
        Assert.True(received.Single(r => r.Message.MessageId == "o-0").At < ShortDelay, "A message of another group was held back.");
        Assert.Equal(1, received.Single(r => r.Message.MessageId == "g-0").Message.DeliveryCount);
    }

    [Fact]
    public async Task Fifo_GroupKeysAndDeduplicationIdsThatSqsWouldReject_AreMapped()
    {
        // Arrange
        // SQS allows printable ASCII without spaces; Foundatio allows any 128 characters.
        string groupKey = "group with spaces ✓ " + new string('g', 100);
        string deduplicationId = "dedup id ✓ " + new string('d', 100);
        Assert.InRange(Math.Max(groupKey.Length, deduplicationId.Length), 100, OutgoingMessage.MaxIdLength);
        var source = MessageSource.Queue(NewName("fifo-ids"));
        await using var receiver = await CreateReceiverAsync(source);

        // Act
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("one", "first") with { GroupKey = groupKey, DeduplicationId = deduplicationId }], TestCancellationToken);
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("two", "second") with { GroupKey = groupKey, DeduplicationId = deduplicationId }], TestCancellationToken);

        // Assert
        var message = await ReceiveOneAsync(receiver);
        Assert.Equal("first", message.MessageId);
        Assert.Equal(groupKey, message.GroupKey);
        await receiver.CompleteAsync(message, TestCancellationToken);
        await AssertNothingReceivedAsync(receiver, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Fifo_MessageDeadLetteredAgainAfterAReplay_IsNotDroppedAsADuplicate()
    {
        // Arrange
        var source = MessageSource.Queue(NewName("fifo-dlq-twice"));
        await using var receiver = await CreateReceiverAsync(source);

        // Act: the same message fails, is replayed with its id, and fails again within the deduplication window.
        for (int i = 0; i < 2; i++)
        {
            await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("poison", "id-poison")], TestCancellationToken);
            await receiver.DeadLetterAsync(await ReceiveOneAsync(receiver), $"attempt {i}", TestCancellationToken);
        }

        // Assert
        await using var deadLetters = await CreateReceiverAsync(MessageSource.DeadLetter(source));
        var messages = await ReceiveAsync(deadLetters, 2);
        Assert.Equal(["attempt 0", "attempt 1"], messages.Select(m => m.Headers[KnownHeaders.DeadLetterReason]).Order());
        Assert.All(messages, m => Assert.Equal("poison", Encoding.UTF8.GetString(m.Body.Span)));
    }
}
