using System.Linq;
using System.Threading.Tasks;
using Foundatio.Messaging.Transport;
using Xunit;

namespace Foundatio.AzureServiceBus.Tests.Messaging;

/// <summary>Runs the conformance suite and the Service Bus tests against queues and topics with duplicate detection, plus what only it does.</summary>
public class AzureServiceBusDuplicateDetectionMessageTransportTests(ITestOutputHelper output) : AzureServiceBusMessageTransportTests(output)
{
    protected override bool RequiresDuplicateDetection => true;

    [Fact]
    public async Task Deduplication_DeduplicationIdIsTheServiceBusMessageId_AndTheMessageIdStillRoundTrips()
    {
        // Arrange
        var source = MessageSource.Queue(NewName("dedupe-ids"));
        await using var receiver = await CreateReceiverAsync(source);

        // Act
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("deduplicated", "id-message") with { DeduplicationId = "dedupe-id" }, Message("plain", "id-plain")], TestCancellationToken);

        // Assert
        var messages = (await ReceiveAsync(receiver, 2)).Cast<AzureServiceBusIncomingMessage>().ToDictionary(m => m.MessageId);
        Assert.Equal("dedupe-id", messages["id-message"].Received.MessageId);
        Assert.NotEqual("id-plain", messages["id-plain"].Received.MessageId);
    }

    [Fact]
    public async Task Deduplication_SendToOneSubscriptionAndPublishWithTheSameId_AreNotDuplicates()
    {
        // Arrange
        string topic = NewName("dedupe-sub");
        await using var receiver = await CreateReceiverAsync(MessageSource.Subscription(topic, "target"));

        // Act
        await Transport.SendAsync(MessageDestination.Subscription(topic, "target"), [Message("direct", "id-direct") with { DeduplicationId = "same" }], TestCancellationToken);
        await Transport.SendAsync(MessageDestination.Topic(topic), [Message("published", "id-published") with { DeduplicationId = "same" }], TestCancellationToken);
        await Transport.SendAsync(MessageDestination.Subscription(topic, "target"), [Message("repeat", "id-repeat") with { DeduplicationId = "same" }, Message("canary", "canary")], TestCancellationToken);

        // Assert
        var messages = await ReceiveUntilAsync(receiver, "canary");
        Assert.Equal(["canary", "id-direct", "id-published"], messages.Select(m => m.MessageId).Order());
    }
}
