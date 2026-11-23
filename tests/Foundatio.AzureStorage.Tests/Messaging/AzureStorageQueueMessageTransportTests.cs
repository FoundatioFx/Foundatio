using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Storage.Queues;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;
using Foundatio.Tests.Messaging;
using Foundatio.Tests.Utility;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Foundatio.AzureStorage.Tests.Messaging;

/// <summary>
/// Runs the conformance suite against Azurite, plus what only the Storage Queues transport does. Each test gets its own
/// queue name prefix, and its queues are deleted when it ends, since the transport only clears the queues it deletes.
/// </summary>
public class AzureStorageQueueMessageTransportTests(ITestOutputHelper output) : MessageTransportTestsBase(output)
{
    private readonly string _prefix = $"ft{Guid.NewGuid():N}"[..10] + "-";
    private QueueServiceClient? _service;

    protected override IMessageTransport CreateTransport() => new AzureStorageQueueMessageTransport(CreateOptions());

    protected override IMessageTransport CreateTransportWithClock(TimeProvider timeProvider) => new AzureStorageQueueMessageTransport(CreateOptions() with { TimeProvider = timeProvider });

    /// <summary>A client of its own, as another producer or consumer of the account would use.</summary>
    protected QueueServiceClient Service => _service ??= new QueueServiceClient(GetConnectionString());

    protected AzureStorageQueueMessageTransportOptions CreateOptions() => new()
    {
        ConnectionString = GetConnectionString(),
        Prefix = _prefix,
        LoggerFactory = Log
    };

    internal AzureStorageQueueMessageTransportNames Names => new(_prefix);

    [Fact]
    public async Task Renew_GivesTheMessageANewPopReceipt_AndLaterSettlementsUseIt()
    {
        // Arrange
        var source = MessageSource.Queue(NewName("receipt"));
        await using var receiver = await CreateReceiverAsync(source, TimeSpan.FromSeconds(30));
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("renewed", "id-renewed")], TestCancellationToken);
        var message = Assert.IsType<AzureStorageQueueIncomingMessage>(await ReceiveOneAsync(receiver));
        string first = message.PopReceipt;

        // Act
        for (int i = 0; i < 3; i++)
            await receiver.RenewAsync(message, TestCancellationToken);
        string renewed = message.PopReceipt;

        // Assert
        Assert.NotEqual(first, renewed);
        var queue = Service.GetQueueClient(Names.Queue(source.Name));
        var stale = await Assert.ThrowsAsync<RequestFailedException>(() => queue.DeleteMessageAsync(message.QueueMessageId, first, TestCancellationToken));
        Assert.Equal(400, stale.Status);
        await receiver.CompleteAsync(message, TestCancellationToken);
        Assert.Equal(0, (await queue.GetPropertiesAsync(TestCancellationToken)).Value.ApproximateMessagesCount);
    }

    [Fact]
    public async Task ConcurrentRenewalsAndComplete_OfOneDelivery_UseTheLatestPopReceipt()
    {
        // Arrange
        var source = MessageSource.Queue(NewName("receipt-race"));
        await using var receiver = await CreateReceiverAsync(source, TimeSpan.FromSeconds(30));
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("raced", "id-raced")], TestCancellationToken);
        var message = await ReceiveOneAsync(receiver);

        // Act
        var renewals = Enumerable.Range(0, 5).Select(_ => receiver.RenewAsync(message, TestCancellationToken)).ToList();
        await Task.WhenAll(renewals);
        await receiver.CompleteAsync(message, TestCancellationToken);

        // Assert
        await AssertNothingReceivedAsync(receiver, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Renew_CancelledAfterStorageAppliedIt_LetsTheCompleteUseItsPopReceipt()
    {
        // Arrange: Storage applies the renewal, then its response is slow, and the caller stops waiting for it, as core
        // does when the handler returns while a renewal is in flight.
        var slow = new SlowMessageUpdateResponses();
        await using var transport = new AzureStorageQueueMessageTransport(CreateOptions() with { ConfigureClient = o => o.AddPolicy(slow, HttpPipelinePosition.PerRetry) });
        var source = MessageSource.Queue(NewName("renew-cancel"));
        DeleteAfterTest(source);
        await using var receiver = await ((IMessageTransport)transport).CreateReceiverAsync(source, new ReceiverOptions { LeaseDuration = TimeSpan.FromSeconds(30) }, TestCancellationToken);
        await ((IMessageTransport)transport).SendAsync(MessageDestination.Queue(source.Name), [Message("renewed", "id-renewed")], TestCancellationToken);
        var message = await ReceiveOneAsync(receiver);
        slow.Enabled = true;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        // Act
        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => receiver.RenewAsync(message, cancellation.Token));
        var cancelledAfter = stopwatch.Elapsed;
        slow.Enabled = false;
        await receiver.CompleteAsync(message, TestCancellationToken);

        // Assert
        Assert.True(cancelledAfter < TimeSpan.FromMilliseconds(400), $"The cancelled renewal returned after {cancelledAfter}, not when it was cancelled.");
        var queue = Service.GetQueueClient(Names.Queue(source.Name));
        Assert.Equal(0, (await queue.GetPropertiesAsync(TestCancellationToken)).Value.ApproximateMessagesCount);
    }

    [Fact]
    public async Task DeadLetter_GoesToThePoisonQueueNamedAfterTheSource()
    {
        // Arrange
        var source = MessageSource.Queue(NewName("poison"));
        await using var receiver = await CreateReceiverAsync(source);
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("poison", "id-poison")], TestCancellationToken);

        // Act
        await receiver.DeadLetterAsync(await ReceiveOneAsync(receiver), "bad input", TestCancellationToken);

        // Assert
        string queueName = Names.Queue(source.Name);
        Assert.Equal(queueName + "-poison", Names.DeadLetter(source));
        var poison = Service.GetQueueClient(queueName + "-poison");
        var peeked = Assert.Single((await poison.PeekMessagesAsync(10, TestCancellationToken)).Value);
        Assert.True(AzureStorageQueueMessageTransportCodec.TryDecode(peeked.Body.ToMemory(), out var body, out string? id, out var headers, out _));
        Assert.Equal("id-poison", id);
        Assert.Equal("poison", Encoding.UTF8.GetString(body.Span));
        Assert.Equal("bad input", headers![KnownHeaders.DeadLetterReason]);
    }

    [Fact]
    public async Task Receive_WhileWaiting_IsWokenAtOnceBySendsAndRetriesThroughTheSameTransport()
    {
        // Arrange: polling every 30 seconds at most, so only the in-process signal can deliver promptly.
        await using var transport = new AzureStorageQueueMessageTransport(CreateOptions() with { MaxPollInterval = TimeSpan.FromSeconds(30) });
        var source = MessageSource.Queue(NewName("signal"));
        DeleteAfterTest(source);
        await using var receiver = await transport.CreateReceiverAsync(source, new ReceiverOptions { LeaseDuration = TimeSpan.FromSeconds(30) }, TestCancellationToken);
        Assert.Empty(await receiver.ReceiveAsync(10, TimeSpan.FromSeconds(4), TestCancellationToken));

        // Act
        var waiting = receiver.ReceiveAsync(10, TimeSpan.FromSeconds(20), TestCancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(500), TestCancellationToken);
        var stopwatch = Stopwatch.StartNew();
        await transport.SendAsync(MessageDestination.Queue(source.Name), [Message("now", "id-now")], TestCancellationToken);
        var message = Assert.Single(await waiting);
        var sendLatency = stopwatch.Elapsed;

        var retrying = receiver.ReceiveAsync(10, TimeSpan.FromSeconds(20), TestCancellationToken);
        stopwatch.Restart();
        await receiver.AbandonAsync(message, TimeSpan.Zero, TestCancellationToken);
        var retried = Assert.Single(await retrying);
        var retryLatency = stopwatch.Elapsed;

        // Assert
        Assert.Equal("id-now", retried.MessageId);
        Assert.True(sendLatency < TimeSpan.FromSeconds(2), $"A waiting receive got the message {sendLatency} after its send began.");
        Assert.True(retryLatency < TimeSpan.FromSeconds(2), $"A waiting receive got the retried message {retryLatency} after the abandon began.");
        await receiver.CompleteAsync(retried, TestCancellationToken);
    }

    [Fact]
    public async Task Receive_OfAMessageFromAnotherProcess_WaitsAtMostAboutTheMaxPollInterval()
    {
        // Arrange
        var interval = TimeSpan.FromMilliseconds(500);
        await using var transport = new AzureStorageQueueMessageTransport(CreateOptions() with { MaxPollInterval = interval });
        var source = MessageSource.Queue(NewName("poll"));
        DeleteAfterTest(source);
        await using var receiver = await transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken);
        Assert.Empty(await receiver.ReceiveAsync(10, TimeSpan.FromSeconds(2), TestCancellationToken));

        // Act
        var waiting = receiver.ReceiveAsync(10, TimeSpan.FromSeconds(10), TestCancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(300), TestCancellationToken);
        var stopwatch = Stopwatch.StartNew();
        await PeerTransport.SendAsync(MessageDestination.Queue(source.Name), [Message("polled", "id-polled")], TestCancellationToken);
        var message = Assert.Single(await waiting);

        // Assert
        Assert.True(stopwatch.Elapsed < interval + TimeSpan.FromMilliseconds(500), $"The receive got the message {stopwatch.Elapsed} after it was sent, with a {interval} poll interval.");
        await receiver.CompleteAsync(message, TestCancellationToken);
    }

    [Theory]
    [InlineData(600)]
    [InlineData(2000)]
    public async Task Send_WithMoreHeadersThanTheFramingReserve_ThrowsMessageTooLarge(int headerCount)
    {
        // Arrange: each header costs two length bytes that MessageSize does not count, and 600 of them leave no room
        // for a dead-letter reason, which would otherwise make the message impossible to dead-letter whole.
        var headers = Enumerable.Range(0, headerCount).ToDictionary(i => $"h{i}", _ => "v");
        long counted = MessageSize.Of("too-many-headers", ReadOnlySpan<byte>.Empty, headers);
        var body = new byte[Transport.Capabilities.MaxMessageSize!.Value - counted];
        var source = MessageSource.Queue(NewName("framing"));
        DeleteAfterTest(source);

        // Act
        var ex = await Assert.ThrowsAsync<MessageTooLargeException>(() => Transport.SendAsync(MessageDestination.Queue(source.Name),
            [new OutgoingMessage { MessageId = "too-many-headers", Body = body, Headers = headers }], TestCancellationToken));

        // Assert
        Assert.Equal(AzureStorageQueueMessageTransportCodec.MaxSendEnvelopeSize, ex.Limit);
        Assert.Contains("headers", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_OverTheSizeLimit_ThrowsMessageTooLargePointingToFileStorage()
    {
        var source = MessageSource.Queue(NewName("too-large"));
        DeleteAfterTest(source);

        var ex = await Assert.ThrowsAsync<MessageTooLargeException>(() => Transport.SendAsync(MessageDestination.Queue(source.Name),
            [new OutgoingMessage { MessageId = "large", Body = new byte[64 * 1024] }], TestCancellationToken));

        Assert.Equal(46 * 1024, ex.Limit);
        Assert.Contains("file storage", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Receive_MessagesWrittenByOtherProducers_AreDeliveredAsTheirBody()
    {
        // Arrange: the Storage SDK's default base64 encoding (also what Event Grid and Azure Functions write), and plain text.
        var source = MessageSource.Queue(NewName("foreign"));
        await using var receiver = await CreateReceiverAsync(source);
        string queueName = Names.Queue(source.Name);
        var base64 = new QueueClient(GetConnectionString(), queueName, new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 });
        var plain = new QueueClient(GetConnectionString(), queueName, new QueueClientOptions { MessageEncoding = QueueMessageEncoding.None });
        var sent = await base64.SendMessageAsync(BinaryData.FromString("{\"value\":1}"), cancellationToken: TestCancellationToken);
        await plain.SendMessageAsync("plain <text> & more", cancellationToken: TestCancellationToken);

        // Act
        var messages = await ReceiveAsync(receiver, 2);

        // Assert
        var bodies = messages.Select(m => Encoding.UTF8.GetString(m.Body.Span)).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["plain <text> & more", "{\"value\":1}"], bodies);
        var json = messages.Single(m => m.Body.Span[0] == (byte)'{');
        Assert.Equal(sent.Value.MessageId, json.MessageId);
        Assert.Empty(json.Headers);
        foreach (var message in messages)
            await receiver.CompleteAsync(message, TestCancellationToken);
    }

    [Theory]
    [InlineData(false, 60_000)]
    [InlineData(true, 49_000)]
    public async Task DeadLetter_OfALargeMessageFromAnotherProducer_KeepsItWholeWithItsIdAndReason(bool base64, int size)
    {
        // Arrange: Storage takes up to 64 KiB of text from another producer, more than an envelope of this transport holds.
        var source = MessageSource.Queue(NewName("dl-foreign"));
        await using var receiver = await CreateReceiverAsync(source);
        var producer = new QueueClient(GetConnectionString(), Names.Queue(source.Name), new QueueClientOptions { MessageEncoding = base64 ? QueueMessageEncoding.Base64 : QueueMessageEncoding.None });
        var body = Encoding.UTF8.GetBytes("{" + new string('a', size - 2) + "}");
        var sent = await producer.SendMessageAsync(BinaryData.FromBytes(body), cancellationToken: TestCancellationToken);
        var message = await ReceiveOneAsync(receiver);
        Assert.Equal(body.Length, message.Body.Length);

        // Act
        await receiver.DeadLetterAsync(message, "Could not parse: " + new string('r', 1000), TestCancellationToken);

        // Assert
        await using var deadLetters = await CreateReceiverAsync(MessageSource.DeadLetter(source));
        var deadLetter = await ReceiveOneAsync(deadLetters);
        Assert.True(deadLetter.Body.Span.SequenceEqual(body), $"The dead letter's body is {deadLetter.Body.Length} bytes, not the {body.Length} sent.");
        Assert.Equal(sent.Value.MessageId, deadLetter.MessageId);
        Assert.StartsWith("Could not parse: ", deadLetter.Headers[KnownHeaders.DeadLetterReason]);
        await AssertNothingReceivedAsync(receiver, TimeSpan.FromMilliseconds(200));
        Assert.Equal(0, (await Service.GetQueueClient(Names.Queue(source.Name)).GetPropertiesAsync(TestCancellationToken)).Value.ApproximateMessagesCount);
    }

    [Fact]
    public async Task DeadLetter_OfAMessageFromAnotherProducerThatFillsAStorageMessage_KeepsItsText()
    {
        // Arrange: no room is left for the dead letter's id and reason.
        var source = MessageSource.Queue(NewName("dl-full"));
        await using var receiver = await CreateReceiverAsync(source);
        string text = new('~', AzureStorageQueueMessageTransportCodec.MaxMessageTextLength - 16);
        await new QueueClient(GetConnectionString(), Names.Queue(source.Name)).SendMessageAsync(text, TestCancellationToken);

        // Act
        await receiver.DeadLetterAsync(await ReceiveOneAsync(receiver), "too big", TestCancellationToken);

        // Assert
        await using var deadLetters = await CreateReceiverAsync(MessageSource.DeadLetter(source));
        var deadLetter = await ReceiveOneAsync(deadLetters);
        Assert.Equal(text, Encoding.UTF8.GetString(deadLetter.Body.Span));
    }

    [Fact]
    public void DeadLetter_OfAnEnvelopeThatFillsAStorageMessage_ThrowsInsteadOfWritingPartOfIt()
    {
        var message = new AzureStorageQueueIncomingMessage
        {
            Owner = null!,
            QueueMessageId = "q",
            PopReceipt = "p",
            MessageId = "full",
            Body = new byte[AzureStorageQueueMessageTransportCodec.MaxEnvelopeSize],
            Headers = new Dictionary<string, string>()
        };

        var ex = Assert.Throws<MessageTooLargeException>(() => AzureStorageQueueMessageTransportCodec.EncodeDeadLetter(message, "reason"));

        Assert.Equal(AzureStorageQueueMessageTransportCodec.MaxEnvelopeSize, ex.Limit);
    }

    [Fact]
    public async Task Names_ThatDifferOnlyInCaseOrPunctuationOrPastTheLengthLimit_DoNotCollide()
    {
        // Arrange
        string name = NewName("collide");
        string tooLong = name + new string('x', 100);
        string[] queues = [$"{name}.a", $"{name}_a", $"{name}-A", tooLong + "1", tooLong + "2"];
        var receivers = new List<IMessageReceiver>();
        foreach (string queue in queues)
            receivers.Add(await CreateReceiverAsync(MessageSource.Queue(queue)));

        // "a-b" + "c" and "a" + "b-c" read the same once joined with '-'.
        var ambiguous = new[] { MessageSource.Subscription($"{name}-t-x", "s"), MessageSource.Subscription($"{name}-t", "x-s") };
        foreach (var subscription in ambiguous)
            receivers.Add(await CreateReceiverAsync(subscription));

        try
        {
            // Act
            foreach (string queue in queues)
                await Transport.SendAsync(MessageDestination.Queue(queue), [Message(queue)], TestCancellationToken);
            foreach (var subscription in ambiguous)
                await Transport.SendAsync(MessageDestination.Topic(subscription.Topic!), [Message(subscription.Topic!)], TestCancellationToken);

            // Assert
            string[] expected = [.. queues, .. ambiguous.Select(s => s.Topic!)];
            for (int i = 0; i < receivers.Count; i++)
            {
                var message = await ReceiveOneAsync(receivers[i]);
                Assert.Equal(expected[i], Encoding.UTF8.GetString(message.Body.Span));
                await receivers[i].CompleteAsync(message, TestCancellationToken);
            }

            foreach (string queueName in queues.Select(Names.Queue).Concat(queues.Select(q => Names.DeadLetter(MessageSource.Queue(q)))))
                Assert.Matches("^[a-z0-9](?!.*--)[a-z0-9-]{1,61}[a-z0-9]$", queueName);
        }
        finally
        {
            foreach (var receiver in receivers)
                await receiver.DisposeAsync();
        }
    }

    [Fact]
    public async Task Publish_SkipsAndRemovesInstanceSubscriptionsWhoseHeartbeatLapsed()
    {
        // Arrange: an instance queue left behind by a process that died an hour ago.
        string topic = NewName("stale");
        await using var live = await CreateReceiverAsync(MessageSource.InstanceSubscription(topic, "live"));
        var stale = Service.GetQueueClient(Names.Instance(topic, "dead"));
        await stale.CreateAsync(new Dictionary<string, string>
        {
            [AzureStorageQueueMessageTransport.KindMetadata] = AzureStorageQueueMessageTransport.InstanceKind,
            [AzureStorageQueueMessageTransport.TopicMetadata] = topic,
            [AzureStorageQueueMessageTransport.HeartbeatMetadata] = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)
        }, TestCancellationToken);

        // Act
        await Transport.SendAsync(MessageDestination.Topic(topic), [Message("event", "id-event")], TestCancellationToken);

        // Assert
        await AssertOnlyReceivedAsync(live, "id-event");
        await WaitForAsync(() => !stale.Exists().Value);
    }

    [Fact]
    public async Task InstanceSubscription_WhenTheClocksOfItsOwnerAndAPublisherDisagree_IsNotRemoved()
    {
        // Arrange: the owner's clock runs 5 seconds behind, the publisher's 5 seconds ahead, with a 6 second timeout.
        string topic = NewName("skew");
        var timeout = TimeSpan.FromSeconds(6);
        await using var owner = new AzureStorageQueueMessageTransport(CreateOptions() with { InstanceSubscriptionTimeout = timeout, TimeProvider = new SkewedTimeProvider(TimeSpan.FromSeconds(-5)) });
        await using var publisher = new AzureStorageQueueMessageTransport(CreateOptions() with { InstanceSubscriptionTimeout = timeout, TimeProvider = new SkewedTimeProvider(TimeSpan.FromSeconds(5)) });
        await using var instance = await ((IMessageTransport)owner).CreateReceiverAsync(MessageSource.InstanceSubscription(topic, "live"), new ReceiverOptions(), TestCancellationToken);

        // Act and assert: publishes over more than a timeout all arrive.
        var stopwatch = Stopwatch.StartNew();
        for (int i = 0; stopwatch.Elapsed < timeout + TimeSpan.FromSeconds(2); i++)
        {
            await ((IMessageTransport)publisher).SendAsync(MessageDestination.Topic(topic), [Message("event", $"id-{i}")], TestCancellationToken);
            await AssertOnlyReceivedAsync(instance, $"id-{i}");
            await Task.Delay(TimeSpan.FromMilliseconds(500), TestCancellationToken);
        }
    }

    [Fact]
    public async Task Publish_WhenOneSubscriptionQueueFails_SendsToTheOthersAndNamesTheFailedQueue()
    {
        // Arrange: sends to the billing subscription's queue are refused.
        string topic = NewName("partial");
        string failing = Names.Subscription(topic, "billing");
        var refuse = new RefuseSendsTo(failing);
        await using var transport = new AzureStorageQueueMessageTransport(CreateOptions() with { ConfigureClient = o => o.AddPolicy(refuse, HttpPipelinePosition.PerCall) });
        await using var billing = await CreateReceiverAsync(MessageSource.Subscription(topic, "billing"));
        await using var shipping = await CreateReceiverAsync(MessageSource.Subscription(topic, "shipping"));
        await using var instance = await CreateReceiverAsync(MessageSource.InstanceSubscription(topic, "live"));

        // Act
        var ex = await Assert.ThrowsAsync<MessageBusException>(() => ((IMessageTransport)transport).SendAsync(MessageDestination.Topic(topic), [Message("event", "id-event")], TestCancellationToken));

        // Assert
        Assert.Contains(failing, ex.Message, StringComparison.Ordinal);
        Assert.Contains("1 of the 3", ex.Message, StringComparison.Ordinal);
        await AssertOnlyReceivedAsync(shipping, "id-event");
        await AssertOnlyReceivedAsync(instance, "id-event");
        await AssertNothingReceivedAsync(billing, TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public async Task DisposingAnInstanceReceiver_DeletesItsQueue()
    {
        string topic = NewName("instance-gone");
        var receiver = await CreateReceiverAsync(MessageSource.InstanceSubscription(topic, "gone"));
        var queues = await ListQueuesAsync(Names.TopicPrefix(topic));
        Assert.Single(queues);

        await receiver.DisposeAsync();

        Assert.Empty(await ListQueuesAsync(Names.TopicPrefix(topic)));
    }

    [Fact]
    public async Task Delete_ClearsTheQueuesAndKeepsThem()
    {
        // Arrange
        var source = MessageSource.Queue(NewName("cleared"));
        await using (var receiver = await CreateReceiverAsync(source))
        {
            await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("a"), Message("b") with { DeliverAt = DateTimeOffset.UtcNow.AddMinutes(5) }], TestCancellationToken);
            await receiver.DeadLetterAsync(await ReceiveOneAsync(receiver), "conformance", TestCancellationToken);
        }

        // Act
        await Transport.DeleteAsync(source, TestCancellationToken);

        // Assert
        var queue = Service.GetQueueClient(Names.Queue(source.Name));
        var poison = Service.GetQueueClient(Names.DeadLetter(source));
        Assert.True((await queue.ExistsAsync(TestCancellationToken)).Value);
        Assert.Equal(0, (await queue.GetPropertiesAsync(TestCancellationToken)).Value.ApproximateMessagesCount);
        Assert.Equal(0, (await poison.GetPropertiesAsync(TestCancellationToken)).Value.ApproximateMessagesCount);
    }

    [Fact]
    public async Task GetStats_CountsLeasedAndDelayedMessagesAsQueued()
    {
        var source = MessageSource.Queue(NewName("stats-all"));
        await using var receiver = await CreateReceiverAsync(source);
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("leased"), Message("delayed") with { DeliverAt = DateTimeOffset.UtcNow.AddMinutes(5) }], TestCancellationToken);
        var leased = await ReceiveOneAsync(receiver);

        var stats = await Transport.GetStatsAsync(source, TestCancellationToken);

        Assert.NotNull(stats);
        Assert.Equal(2, stats.Queued);
        Assert.Null(stats.InFlight);
        Assert.Null(stats.Delayed);
        Assert.Equal(0, stats.DeadLettered);
        await receiver.CompleteAsync(leased, TestCancellationToken);
    }

    [Fact]
    public async Task MessageTimeToLive_LimitsDelaysAndIsAppliedToMessages()
    {
        await using var transport = new AzureStorageQueueMessageTransport(CreateOptions() with { MessageTimeToLive = TimeSpan.FromHours(1) });
        var source = MessageSource.Queue(NewName("ttl"));
        DeleteAfterTest(source);

        Assert.Equal(TimeSpan.FromHours(1) - TimeSpan.FromSeconds(1), transport.Capabilities.MaxDelay);
        Assert.Equal(TimeSpan.FromHours(1) - TimeSpan.FromSeconds(1), transport.Capabilities.MaxRetryDelay);
        Assert.Equal(TimeSpan.FromHours(1) - TimeSpan.FromSeconds(1), transport.Capabilities.MaxLeaseDuration);
        await transport.SendAsync(MessageDestination.Queue(source.Name), [Message("expiring")], TestCancellationToken);

        var peeked = Assert.Single((await Service.GetQueueClient(Names.Queue(source.Name)).PeekMessagesAsync(1, TestCancellationToken)).Value);
        Assert.NotNull(peeked.ExpiresOn);
        Assert.InRange(peeked.ExpiresOn!.Value, DateTimeOffset.UtcNow.AddMinutes(55), DateTimeOffset.UtcNow.AddMinutes(65));
    }

    [Fact]
    public async Task DeadLetter_WithAMessageTimeToLive_NeverExpires()
    {
        // Arrange
        await using var transport = new AzureStorageQueueMessageTransport(CreateOptions() with { MessageTimeToLive = TimeSpan.FromHours(1) });
        var source = MessageSource.Queue(NewName("dl-ttl"));
        DeleteAfterTest(source);
        await using var receiver = await ((IMessageTransport)transport).CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken);
        await ((IMessageTransport)transport).SendAsync(MessageDestination.Queue(source.Name), [Message("poison")], TestCancellationToken);

        // Act
        await receiver.DeadLetterAsync(await ReceiveOneAsync(receiver), "kept", TestCancellationToken);

        // Assert
        var peeked = Assert.Single((await Service.GetQueueClient(Names.DeadLetter(source)).PeekMessagesAsync(1, TestCancellationToken)).Value);
        Assert.True(peeked.ExpiresOn is null || peeked.ExpiresOn > DateTimeOffset.UtcNow.AddYears(100), $"The dead letter expires at {peeked.ExpiresOn}.");
    }

    [Fact]
    public async Task AbandonAndRenew_OfAMessageExpiringSooner_KeepItInvisibleOnlyUntilJustBeforeItExpires()
    {
        // Arrange: Storage refuses a visibility timeout past a message's expiry (Azurite does not check it).
        var updates = new MessageUpdateVisibilityTimeouts();
        await using var transport = new AzureStorageQueueMessageTransport(CreateOptions() with
        {
            MessageTimeToLive = TimeSpan.FromSeconds(20),
            ConfigureClient = o => o.AddPolicy(updates, HttpPipelinePosition.PerCall)
        });
        var source = MessageSource.Queue(NewName("expiring"));
        DeleteAfterTest(source);
        await using var receiver = await ((IMessageTransport)transport).CreateReceiverAsync(source, new ReceiverOptions { LeaseDuration = TimeSpan.FromSeconds(60) }, TestCancellationToken);
        await ((IMessageTransport)transport).SendAsync(MessageDestination.Queue(source.Name), [Message("expiring")], TestCancellationToken);
        var message = await ReceiveOneAsync(receiver);

        // Act
        await receiver.RenewAsync(message, TestCancellationToken);
        await receiver.AbandonAsync(message, TimeSpan.FromMinutes(5), TestCancellationToken);

        // Assert
        Assert.Equal(2, updates.Seconds.Count);
        Assert.All(updates.Seconds, seconds => Assert.InRange(seconds, 15, 19));
    }

    [Fact]
    public async Task Send_WithoutATimeToLive_KeepsTheMessageUntilItIsReceived()
    {
        var source = MessageSource.Queue(NewName("no-ttl"));
        DeleteAfterTest(source);

        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("kept")], TestCancellationToken);

        var peeked = Assert.Single((await Service.GetQueueClient(Names.Queue(source.Name)).PeekMessagesAsync(1, TestCancellationToken)).Value);
        Assert.True(peeked.ExpiresOn is null || peeked.ExpiresOn > DateTimeOffset.UtcNow.AddYears(100), $"The message expires at {peeked.ExpiresOn}.");
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        try
        {
            foreach (string queueName in await ListQueuesAsync(_prefix, CancellationToken.None))
                await Service.GetQueueClient(queueName).DeleteIfExistsAsync();
        }
        catch (RequestFailedException ex)
        {
            Log.CreateLogger<AzureStorageQueueMessageTransportTests>().LogWarning(ex, "Could not delete the queues of the test");
        }
    }

    private async Task<List<string>> ListQueuesAsync(string prefix, CancellationToken? cancellationToken = null)
    {
        var names = new List<string>();
        await foreach (var queue in Service.GetQueuesAsync(prefix: prefix, cancellationToken: cancellationToken ?? TestCancellationToken))
            names.Add(queue.Name);

        return names;
    }

    /// <summary>Records the visibility timeout of each Update Message request.</summary>
    private sealed class MessageUpdateVisibilityTimeouts : HttpPipelineSynchronousPolicy
    {
        public List<int> Seconds { get; } = [];

        public override void OnSendingRequest(HttpMessage message)
        {
            var uri = message.Request.Uri.ToUri();
            if (message.Request.Method != RequestMethod.Put || !uri.AbsolutePath.Contains("/messages/", StringComparison.Ordinal))
                return;

            string? value = System.Web.HttpUtility.ParseQueryString(uri.Query)["visibilitytimeout"];
            lock (Seconds)
                Seconds.Add(Int32.Parse(value!, CultureInfo.InvariantCulture));
        }
    }

    /// <summary>Sends the requests that put messages on one queue to an invalid queue name instead, which Storage refuses.</summary>
    private sealed class RefuseSendsTo(string queueName) : HttpPipelineSynchronousPolicy
    {
        public override void OnSendingRequest(HttpMessage message)
        {
            var uri = message.Request.Uri.ToUri();
            if (message.Request.Method == RequestMethod.Post && uri.AbsolutePath.EndsWith($"/{queueName}/messages", StringComparison.Ordinal))
                message.Request.Uri.Reset(new Uri(uri.ToString().Replace($"/{queueName}/", "/Invalid--Name/", StringComparison.Ordinal)));
        }
    }

    private sealed class SkewedTimeProvider(TimeSpan skew) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + skew;
    }

    /// <summary>Delays the responses of Update Message requests after Storage has applied them.</summary>
    private sealed class SlowMessageUpdateResponses : HttpPipelinePolicy
    {
        public volatile bool Enabled;

        public override void Process(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline) => throw new NotSupportedException();

        public override async ValueTask ProcessAsync(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            await ProcessNextAsync(message, pipeline);
            if (Enabled && message.Request.Method == RequestMethod.Put && message.Request.Uri.ToUri().AbsolutePath.Contains("/messages/", StringComparison.Ordinal))
                await Task.Delay(TimeSpan.FromMilliseconds(500), message.CancellationToken);
        }
    }

    internal static string GetConnectionString() =>
        Configuration.GetConnectionString("AzureStorageConnectionString") ?? "UseDevelopmentStorage=true";
}
