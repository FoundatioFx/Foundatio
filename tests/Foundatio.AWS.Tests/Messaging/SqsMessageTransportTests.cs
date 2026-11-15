using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Runtime;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;
using Foundatio.Tests.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using SqsMessageAttributeValue = Amazon.SQS.Model.MessageAttributeValue;

namespace Foundatio.AWS.Tests.Messaging;

public class SqsMessageTransportTests(ITestOutputHelper output) : MessageTransportTestsBase(output)
{
    protected const string NamePrefix = "ft-";
    private const int MaxSqsMessageSize = 256 * 1024;

    private static readonly IConfiguration _configuration = new ConfigurationBuilder()
        .SetBasePath(Directory.GetCurrentDirectory())
        .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
        .AddEnvironmentVariables()
        .Build();

    private readonly List<IDisposable> _clients = [];

    /// <summary>Runs every test against FIFO queues and topics when true.</summary>
    protected virtual bool Fifo => false;

    protected string Suffix => Fifo ? ".fifo" : String.Empty;

    protected override IMessageTransport CreateTransport() => new SqsMessageTransport(CreateOptions());

    [Fact]
    public async Task Names_ThatOnlyDifferInPunctuationOrPastTheLengthLimit_DoNotCollide()
    {
        // Arrange
        string name = NewName("collide");
        string tooLong = name + new string('x', 100);
        string[] queues = [$"{name}.a", $"{name}_a", tooLong + "1", tooLong + "2"];
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
                await AssertNothingReceivedAsync(receivers[i], TimeSpan.FromMilliseconds(500));
            }

            using var sqs = CreateSqsClient();
            var created = (await sqs.ListQueuesAsync(new ListQueuesRequest { QueueNamePrefix = NamePrefix + name, MaxResults = 1000 }, TestCancellationToken)).QueueUrls ?? [];
            var queueNames = created.Select(url => url[(url.LastIndexOf('/') + 1)..]).ToList();
            Assert.Equal(receivers.Count, queueNames.Count);
            Assert.All(queueNames, n => Assert.Matches(Fifo ? "^[A-Za-z0-9_-]{1,75}\\.fifo$" : "^[A-Za-z0-9_-]{1,80}$", n));
        }
        finally
        {
            foreach (var receiver in receivers)
                await receiver.DisposeAsync();
        }
    }

    [Fact]
    public async Task Receive_MessageNotSentByTheTransport_UsesTheRawBodyAndSqsMessageId()
    {
        // Arrange
        var source = MessageSource.Queue(NewName("foreign"));
        await using var receiver = await CreateReceiverAsync(source);
        using var sqs = CreateSqsClient();
        string queueUrl = (await sqs.GetQueueUrlAsync(NamePrefix + source.Name + Suffix, TestCancellationToken)).QueueUrl;
        string[] bodies = ["{\"orderId\":42,\"note\":\"héllo\"}", "aGVsbG8=", "-"];

        // Act
        var sent = new Dictionary<string, string>();
        foreach (string body in bodies)
            sent[(await RawSendAsync(sqs, new SendMessageRequest { QueueUrl = queueUrl, MessageBody = body })).MessageId] = body;

        var notAnEnvelope = await RawSendAsync(sqs, new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "{\"e\":\"base64\"}",
            MessageAttributes = new Dictionary<string, SqsMessageAttributeValue> { ["foundatio"] = new() { DataType = "String", StringValue = "not an envelope" } }
        });
        sent[notAnEnvelope.MessageId] = "{\"e\":\"base64\"}";

        var unknownVersion = await RawSendAsync(sqs, new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "versioned",
            MessageAttributes = new Dictionary<string, SqsMessageAttributeValue> { ["foundatio"] = new() { DataType = "Binary", BinaryValue = new MemoryStream([99, 0, 1, 2]) } }
        });
        sent[unknownVersion.MessageId] = "versioned";

        // Assert
        var messages = await ReceiveAsync(receiver, sent.Count);
        foreach (var message in messages)
        {
            Assert.Equal(sent[message.MessageId], Encoding.UTF8.GetString(message.Body.Span));
            Assert.Empty(message.Headers);
            Assert.Equal(1, message.DeliveryCount);
            Assert.NotNull(message.EnqueuedAt);
            await receiver.CompleteAsync(message, TestCancellationToken);
        }
    }

    [Fact]
    public async Task BinaryBodiesAndUnusualHeaders_RoundTripThroughQueuesAndTopics()
    {
        // Arrange
        string name = NewName("binary");
        await using var queue = await CreateReceiverAsync(MessageSource.Queue(name));
        await using var subscription = await CreateReceiverAsync(MessageSource.Subscription(name, "binary"));
        var headers = new Dictionary<string, string>
        {
            ["quote\"and\\slash"] = "\"quoted\"",
            ["control"] = "a\u0001b\u0000c",
            ["lines"] = "one\r\ntwo\tthree",
            ["emoji"] = "😀 ✓ ü",
            ["json"] = "{\"nested\":[1,2]}"
        };

        var bodies = new Dictionary<string, byte[]>
        {
            ["invalid-utf8"] = [0xFF, 0xFE, 0x00, 0x01, 0x80],
            ["control-text"] = Encoding.UTF8.GetBytes("a\u0001b"),
            ["bom-text"] = [0xEF, 0xBB, 0xBF, (byte)'h', (byte)'i'],
            ["whitespace"] = Encoding.UTF8.GetBytes("  \r\n "),
            ["emoji-text"] = Encoding.UTF8.GetBytes("😀 ✓")
        };
        var messages = bodies.Select(b => new OutgoingMessage { MessageId = b.Key, Body = b.Value, Headers = headers, GroupKey = "group-" + b.Key }).ToList();

        // Act
        await Transport.SendAsync(MessageDestination.Queue(name), messages, TestCancellationToken);
        await Transport.SendAsync(MessageDestination.Topic(name), messages, TestCancellationToken);

        // Assert
        foreach (var receiver in new[] { queue, subscription })
        {
            foreach (var message in await ReceiveAsync(receiver, bodies.Count))
            {
                Assert.True(message.Body.Span.SequenceEqual(bodies[message.MessageId]), $"Body of {message.MessageId} changed.");
                Assert.Equal(headers, message.Headers);
                Assert.Equal("group-" + message.MessageId, message.GroupKey);
                await receiver.CompleteAsync(message, TestCancellationToken);
            }
        }
    }

    [Fact]
    public async Task Send_GroupedMessageToAQueueOrSubscription_SetsTheSqsMessageGroupId()
    {
        // Arrange: on standard queues, SQS fair queues use the group id to keep a busy group (tenant) from delaying the others.
        var transport = CreateInterceptedTransport(out var sqsInterceptor);
        string name = NewName("group-id");
        var destinations = new[] { MessageDestination.Queue(name), MessageDestination.Subscription(name, "sub") };
        await using var queueReceiver = await transport.CreateReceiverAsync(MessageSource.Queue(name), new ReceiverOptions(), TestCancellationToken);
        await using var subscriptionReceiver = await transport.CreateReceiverAsync(MessageSource.Subscription(name, "sub"), new ReceiverOptions(), TestCancellationToken);

        foreach (var destination in destinations)
        {
            // Act
            await transport.SendAsync(destination, [Message("grouped", "id-grouped") with { GroupKey = "tenant-42" }], TestCancellationToken);
            var grouped = Assert.Single(sqsInterceptor.RequestsOf<SendMessageBatchRequest>().Last().Entries);
            await transport.SendAsync(destination, [Message("ungrouped", "id-ungrouped")], TestCancellationToken);
            var ungrouped = Assert.Single(sqsInterceptor.RequestsOf<SendMessageBatchRequest>().Last().Entries);

            // Assert
            Assert.Equal("tenant-42", grouped.MessageGroupId);
            if (!Fifo)
                Assert.Null(ungrouped.MessageGroupId);
        }

        foreach (var receiver in new[] { queueReceiver, subscriptionReceiver })
        {
            var messages = await ReceiveAsync(receiver, 2);
            Assert.Equal("tenant-42", messages.Single(m => m.MessageId == "id-grouped").GroupKey);
            Assert.Null(messages.Single(m => m.MessageId == "id-ungrouped").GroupKey);
        }
    }

    [Fact]
    public async Task Send_BatchOverThePayloadLimit_IsSplitAndDeliversEveryMessage()
    {
        // Arrange
        var source = MessageSource.Queue(NewName("payload"));
        await using var receiver = await CreateReceiverAsync(source);
        string body = new('a', 100 * 1024);
        var messages = Enumerable.Range(0, 10).Select(i => Message(body, $"id-{i}")).ToList();

        // Act
        await Transport.SendAsync(MessageDestination.Queue(source.Name), messages, TestCancellationToken);

        // Assert
        var received = await ReceiveAsync(receiver, messages.Count);
        Assert.Equal(messages.Select(m => m.MessageId).Order(), received.Select(m => m.MessageId).Order());
        Assert.All(received, m => Assert.Equal(body.Length, m.Body.Length));
    }

    [Fact]
    public async Task Publish_DelayedBeyondTheLongestVisibilityTimeout_IsHiddenUntilDue()
    {
        // Arrange: real SQS rejects a visibility that ends more than 12 hours after the receive; the emulator does not.
        var transport = CreateInterceptedTransport(out var sqsInterceptor);
        sqsInterceptor.EnforceVisibilityLimit = true;
        string topic = NewName("long-delay");
        await using var receiver = await transport.CreateReceiverAsync(MessageSource.Subscription(topic, "later"), new ReceiverOptions { LeaseDuration = TimeSpan.FromSeconds(1) }, TestCancellationToken);
        using var sqs = CreateSqsClient();
        string queueUrl = Assert.Single((await sqs.ListQueuesAsync(new ListQueuesRequest { QueueNamePrefix = $"{NamePrefix}{topic}-later" }, TestCancellationToken)).QueueUrls ?? []);

        // Act: SQS hides a message for at most 12 hours, so this one is hidden again when it next appears.
        await transport.SendAsync(MessageDestination.Topic(topic), [Message("tomorrow") with { DeliverAt = DateTimeOffset.UtcNow.AddHours(13) }], TestCancellationToken);

        // Assert: the message stays hidden after its 1-second lease, so it is not received again.
        await AssertNothingReceivedAsync(receiver, TimeSpan.FromSeconds(4));
        Assert.Equal(0, sqsInterceptor.RejectedVisibilityChanges);
        var hide = Assert.Single(sqsInterceptor.RequestsOf<ChangeMessageVisibilityRequest>());
        Assert.InRange(hide.VisibilityTimeout.GetValueOrDefault(), (int)TimeSpan.FromHours(11).TotalSeconds, (int)TimeSpan.FromHours(12).TotalSeconds - 1);
        var attributes = await sqs.GetQueueAttributesAsync(queueUrl, [QueueAttributeName.ApproximateNumberOfMessagesNotVisible, QueueAttributeName.ApproximateNumberOfMessages], TestCancellationToken);
        Assert.Equal("1", attributes.Attributes[QueueAttributeName.ApproximateNumberOfMessagesNotVisible]);
        Assert.Equal("0", attributes.Attributes[QueueAttributeName.ApproximateNumberOfMessages]);
    }

    [Fact]
    public async Task Publish_DelayedForSeveralVisibilityTimeouts_IsDeliveredWhenDueAsTheFirstAttempt()
    {
        // Arrange: each time the message is hidden, the clock jumps ahead by the hide and SQS shows it again a second later.
        var clock = new OffsetTimeProvider();
        var transport = CreateInterceptedTransport(out var sqsInterceptor, clock);
        sqsInterceptor.EnforceVisibilityLimit = true;
        var hides = new List<int>();
        sqsInterceptor.Intercept = (request, send) =>
        {
            if (request is ChangeMessageVisibilityRequest { VisibilityTimeout: > 1 } change)
            {
                lock (hides)
                    hides.Add(change.VisibilityTimeout.Value);
                clock.Advance(TimeSpan.FromSeconds(change.VisibilityTimeout.Value));
                change.VisibilityTimeout = 1;
            }

            return send();
        };

        string topic = NewName("hops");
        await using var receiver = await transport.CreateReceiverAsync(MessageSource.Subscription(topic, "later"), new ReceiverOptions { LeaseDuration = TimeSpan.FromSeconds(30) }, TestCancellationToken);

        // Act
        await transport.SendAsync(MessageDestination.Topic(topic), [Message("in 30 hours", "id-30h") with { DeliverAt = clock.GetUtcNow().AddHours(30) }], TestCancellationToken);
        var message = await ReceiveOneAsync(receiver, TimeSpan.FromSeconds(30));

        // Assert: three hides of at most 12 hours from their receives, none counted as an attempt.
        Assert.Equal("id-30h", message.MessageId);
        Assert.Equal(1, message.DeliveryCount);
        Assert.Equal(0, sqsInterceptor.RejectedVisibilityChanges);
        Assert.Equal(3, hides.Count);
        Assert.True(clock.GetUtcNow() >= message.EnqueuedAt!.Value.AddHours(30).AddSeconds(-1), $"Delivered at {clock.GetUtcNow()}, before it was due.");
    }

    [Fact]
    public async Task Receive_EarlyMessageWhileManyAreInFlight_IsSentAgainWithADelayInsteadOfHidden()
    {
        Assert.SkipWhen(Fifo, "FIFO queues cannot delay a single message, so an early one is always hidden.");

        // Arrange: SQS stops returning messages from a queue with about 120,000 in flight. Hidden messages count;
        // delayed ones do not. Each delay moves the clock ahead by the delay and SQS shows the copy a second later.
        var clock = new OffsetTimeProvider();
        var transport = CreateInterceptedTransport(out var sqsInterceptor, clock);
        var delays = new List<int>();
        sqsInterceptor.Intercept = async (request, send) =>
        {
            if (request is SendMessageRequest { DelaySeconds: > 1 } copy)
            {
                lock (delays)
                    delays.Add(copy.DelaySeconds.Value);
                clock.Advance(TimeSpan.FromSeconds(copy.DelaySeconds.Value));
                copy.DelaySeconds = 1;
            }

            var response = await send();
            if (request is GetQueueAttributesRequest attributes && attributes.AttributeNames.Contains(QueueAttributeName.ApproximateNumberOfMessagesNotVisible))
                ((GetQueueAttributesResponse)response).Attributes[QueueAttributeName.ApproximateNumberOfMessagesNotVisible] = "100000";

            return response;
        };

        string topic = NewName("in-flight");
        await using var receiver = await transport.CreateReceiverAsync(MessageSource.Subscription(topic, "later"), new ReceiverOptions(), TestCancellationToken);
        var headers = new Dictionary<string, string> { ["tenant"] = "42" };

        // Act
        await transport.SendAsync(MessageDestination.Topic(topic), [Message("in 20 minutes", "id-later", headers) with { GroupKey = "g", DeliverAt = clock.GetUtcNow().AddMinutes(20) }], TestCancellationToken);
        var message = await ReceiveOneAsync(receiver, TimeSpan.FromSeconds(30));

        // Assert
        Assert.Equal("id-later", message.MessageId);
        Assert.Equal("in 20 minutes", Encoding.UTF8.GetString(message.Body.Span));
        Assert.Equal(headers, message.Headers);
        Assert.Equal("g", message.GroupKey);
        Assert.Equal(1, message.DeliveryCount);
        Assert.Equal(2, delays.Count);
        Assert.Equal(900, delays[0]);
        Assert.InRange(delays[1], 280, 300);
        Assert.DoesNotContain(sqsInterceptor.RequestsOf<ChangeMessageVisibilityRequest>(), r => r.VisibilityTimeout > 0);
        await AssertNothingReceivedAsync(receiver, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task SendAsync_CommandDelayedLongerThanSqsDelaysNatively_IsHiddenUntilDueInsteadOfSentAgain()
    {
        // Arrange: each native delay and each hide moves the clock ahead by its length, and SQS shows the message a
        // second later. A command that core sent again every 15 minutes would show up as more sends.
        var clock = new OffsetTimeProvider();
        var transport = CreateInterceptedTransport(out var sqsInterceptor, clock);
        sqsInterceptor.EnforceVisibilityLimit = true;
        var hides = new ConcurrentQueue<int>();
        sqsInterceptor.Intercept = (request, send) =>
        {
            if (request is SendMessageBatchRequest batch)
            {
                foreach (var entry in batch.Entries.Where(e => e.DelaySeconds > 1))
                {
                    clock.Advance(TimeSpan.FromSeconds(entry.DelaySeconds!.Value));
                    entry.DelaySeconds = 1;
                }
            }
            else if (request is ChangeMessageVisibilityRequest { VisibilityTimeout: > 1 } change)
            {
                hides.Enqueue(change.VisibilityTimeout.Value);
                clock.Advance(TimeSpan.FromSeconds(change.VisibilityTimeout.Value));
                change.VisibilityTimeout = 1;
            }

            return send();
        };

        string name = NewName("long-send");
        DeleteAfterTest(MessageSource.Queue(name));
        var handled = new TaskCompletionSource<(DateTimeOffset At, int Attempt)>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await MessagingTestHost.StartAsync(transport, Log, "worker", m => m
            .MapMessage<ConformanceMessage>(name)
            .AddConsumer<ConformanceMessage>((_, context, _) =>
            {
                handled.TrySetResult((clock.GetUtcNow(), context.Attempt));
                return Task.CompletedTask;
            }), services => services.AddSingleton<TimeProvider>(clock));
        var due = clock.GetUtcNow().AddHours(2);

        // Act
        await host.Client.SendAsync(new ConformanceMessage(1), new SendOptions { DeliverAt = due }, TestCancellationToken);
        var (handledAt, attempt) = await handled.Task.WaitAsync(TimeSpan.FromSeconds(30), TestCancellationToken);

        // Assert: one send, hidden once when it first arrived, and handled when due as the first attempt.
        Assert.True(handledAt >= due.AddSeconds(-1), $"Handled at {handledAt}, before it was due at {due}.");
        Assert.Equal(1, attempt);
        Assert.Single(sqsInterceptor.RequestsOf<SendMessageBatchRequest>());
        Assert.Empty(sqsInterceptor.RequestsOf<SendMessageRequest>());
        Assert.InRange(Assert.Single(hides), (int)TimeSpan.FromMinutes(100).TotalSeconds, (int)TimeSpan.FromHours(2).TotalSeconds);
        Assert.Equal(0, sqsInterceptor.RejectedVisibilityChanges);
    }

    [Fact]
    public async Task ReceiveAsync_ConcurrentCallsOnOneReceiver_HandOutEachMessageOnce()
    {
        // Arrange: publishes carry their delay, so each receive also hides the early messages it gets.
        int concurrency = Transport.Capabilities.MaxConcurrentReceives;
        Assert.True(concurrency > 1, $"MaxConcurrentReceives is {concurrency}.");
        var transport = CreateInterceptedTransport(out var sqsInterceptor);
        int receiving = 0;
        int mostReceiving = 0;
        sqsInterceptor.Intercept = async (request, send) =>
        {
            if (request is not ReceiveMessageRequest)
                return await send();

            int current = Interlocked.Increment(ref receiving);
            int most;
            while (current > (most = Volatile.Read(ref mostReceiving)) && Interlocked.CompareExchange(ref mostReceiving, current, most) != most)
            {
            }

            try
            {
                return await send();
            }
            finally
            {
                Interlocked.Decrement(ref receiving);
            }
        };

        string topic = NewName("concurrent");
        var source = MessageSource.Subscription(topic, "all");
        DeleteAfterTest(source);
        await using var receiver = await transport.CreateReceiverAsync(source, new ReceiverOptions { LeaseDuration = TimeSpan.FromSeconds(30) }, TestCancellationToken);
        var messages = Enumerable.Range(0, 60).Select(i => Message($"m{i}", $"id-{i:D2}") with
        {
            GroupKey = $"g{i % 6}",
            DeliverAt = i % 10 == 0 ? DateTimeOffset.UtcNow.Add(ShortDelay) : null
        }).ToList();

        // Act
        await transport.SendAsync(MessageDestination.Topic(topic), messages, TestCancellationToken);
        var received = await ReceiveConcurrentlyAsync(receiver, concurrency, messages.Count, DeliveryTimeout + ShortDelay);

        // Assert
        await AssertNothingReceivedAsync(receiver, TimeSpan.FromSeconds(2));
        Assert.Equal(messages.Select(m => m.MessageId).Order(), received.Select(m => m.MessageId).Order());
        Assert.True(Volatile.Read(ref mostReceiving) > 1, "The receives never overlapped.");
    }

    [Fact]
    public async Task ReceiveAsync_ConcurrentCallsGetEarlyMessagesWhileManyAreInFlight_SendsEachAgainInsteadOfHidingIt()
    {
        Assert.SkipWhen(Fifo, "FIFO queues cannot delay a single message, so an early one is always hidden.");

        // Arrange: reading the in-flight count takes a second, so the other receives get their early messages while
        // it runs. Each delay moves the clock ahead by the delay and SQS shows the copy a second later. A move makes
        // the count older than a minute, so only reads before the first move show whether concurrent receives share one.
        var clock = new OffsetTimeProvider();
        var transport = CreateInterceptedTransport(out var sqsInterceptor, clock);
        int clockMoves = 0;
        int readsBeforeClockMoved = 0;
        sqsInterceptor.Intercept = async (request, send) =>
        {
            if (request is SendMessageRequest { DelaySeconds: > 1 } copy)
            {
                Interlocked.Increment(ref clockMoves);
                clock.Advance(TimeSpan.FromSeconds(copy.DelaySeconds.Value));
                copy.DelaySeconds = 1;
            }

            bool isInFlightCount = request is GetQueueAttributesRequest attributes && attributes.AttributeNames.Contains(QueueAttributeName.ApproximateNumberOfMessagesNotVisible);
            if (isInFlightCount && Volatile.Read(ref clockMoves) == 0)
                Interlocked.Increment(ref readsBeforeClockMoved);

            if (isInFlightCount)
                await Task.Delay(TimeSpan.FromSeconds(1));

            var response = await send();
            if (isInFlightCount)
                ((GetQueueAttributesResponse)response).Attributes[QueueAttributeName.ApproximateNumberOfMessagesNotVisible] = "100000";

            return response;
        };

        string topic = NewName("in-flight-concurrent");
        var source = MessageSource.Subscription(topic, "later");
        DeleteAfterTest(source);
        await using var receiver = await transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken);
        var messages = Enumerable.Range(0, 4).Select(i => Message($"m{i}", $"id-{i}") with { DeliverAt = clock.GetUtcNow().AddMinutes(20) }).ToList();

        // Act: one message per receive, so each early message goes through a different receive.
        var receiving = ReceiveConcurrentlyAsync(receiver, messages.Count, messages.Count, TimeSpan.FromSeconds(30), maxMessages: 1);
        await transport.SendAsync(MessageDestination.Topic(topic), messages, TestCancellationToken);
        var received = await receiving;

        // Assert
        Assert.Equal(messages.Select(m => m.MessageId).Order(), received.Select(m => m.MessageId).Order());
        Assert.All(received, m => Assert.Equal(1, m.DeliveryCount));
        Assert.DoesNotContain(sqsInterceptor.RequestsOf<ChangeMessageVisibilityRequest>(), r => r.VisibilityTimeout > 0);
        Assert.Equal(messages.Count, sqsInterceptor.RequestsOf<SendMessageRequest>().Count());
        Assert.Equal(1, readsBeforeClockMoved);
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("renew")]
    [InlineData("abandon")]
    public async Task Settle_ManyAtOnceWhileACallIsInFlight_AreSentInBatchesOfTen(string operation)
    {
        // Arrange: the first call is held until every settlement has started, so the others gather behind it.
        var transport = CreateInterceptedTransport(out var sqsInterceptor);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sqsInterceptor.Intercept = async (request, send) =>
        {
            if (request is DeleteMessageRequest or DeleteMessageBatchRequest or ChangeMessageVisibilityRequest or ChangeMessageVisibilityBatchRequest)
                await release.Task;

            return await send();
        };

        var source = MessageSource.Queue(NewName("settle-batch"));
        DeleteAfterTest(source);
        await using var receiver = await transport.CreateReceiverAsync(source, new ReceiverOptions { LeaseDuration = TimeSpan.FromSeconds(30) }, TestCancellationToken);
        await transport.SendAsync(MessageDestination.Queue(source.Name), Enumerable.Range(0, 25).Select(i => Message($"m{i}", $"id-{i:D2}")).ToList(), TestCancellationToken);
        var messages = await ReceiveAsync(receiver, 25);

        // Act
        var settlements = messages.Select(m => Settle(receiver, m, operation)).ToList();
        release.SetResult();
        await Task.WhenAll(settlements);

        // Assert: the first went out at once on its own, the next ones as soon as ten had gathered, the rest when a call returned.
        int singles = operation == "complete" ? sqsInterceptor.RequestsOf<DeleteMessageRequest>().Count() : sqsInterceptor.RequestsOf<ChangeMessageVisibilityRequest>().Count();
        var batches = operation == "complete"
            ? sqsInterceptor.RequestsOf<DeleteMessageBatchRequest>().Select(r => r.Entries.Count)
            : sqsInterceptor.RequestsOf<ChangeMessageVisibilityBatchRequest>().Select(r => r.Entries.Count);
        Assert.Equal(1, singles);
        Assert.Equal([10, 10, 4], batches);
    }

    [Fact]
    public async Task Settle_EntriesThatFailInABatch_FailOnlyTheirOwnCallers()
    {
        // Arrange: the first delete and the first visibility change are held, so the next three of each go out
        // together. SQS then reports the first of those three lost and the second failed.
        var transport = CreateInterceptedTransport(out var sqsInterceptor);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sqsInterceptor.Intercept = async (request, send) =>
        {
            if (request is DeleteMessageRequest or ChangeMessageVisibilityRequest)
                await release.Task;

            var response = await send();
            switch (response)
            {
                case DeleteMessageBatchResponse deleted:
                    deleted.Successful.RemoveAll(e => e.Id is "0" or "1");
                    deleted.Failed = [Failure("0", "ReceiptHandleIsInvalid", senderFault: true), Failure("1", "InternalError", senderFault: false)];
                    break;
                case ChangeMessageVisibilityBatchResponse changed:
                    changed.Successful.RemoveAll(e => e.Id is "0" or "1");
                    changed.Failed = [Failure("0", "AWS.SimpleQueueService.MessageNotInflight", senderFault: true), Failure("1", "InternalError", senderFault: false)];
                    break;
            }

            return response;
        };

        var source = MessageSource.Queue(NewName("settle-fail"));
        DeleteAfterTest(source);
        await using var receiver = await transport.CreateReceiverAsync(source, new ReceiverOptions { LeaseDuration = TimeSpan.FromSeconds(30) }, TestCancellationToken);
        await transport.SendAsync(MessageDestination.Queue(source.Name), Enumerable.Range(0, 8).Select(i => Message($"m{i}", $"id-{i}")).ToList(), TestCancellationToken);
        var messages = await ReceiveAsync(receiver, 8);

        // Act
        var completions = messages.Take(4).Select(m => receiver.CompleteAsync(m, TestCancellationToken)).ToList();
        var renewals = messages.Skip(4).Select(m => receiver.RenewAsync(m, TestCancellationToken)).ToList();
        release.SetResult();

        // Assert
        foreach (var calls in new[] { completions, renewals })
        {
            await calls[0];
            await Assert.ThrowsAsync<MessageLeaseLostException>(() => calls[1]);
            var failed = await Assert.ThrowsAsync<MessageBusException>(() => calls[2]);
            Assert.Equal("InternalError", Assert.IsAssignableFrom<AmazonServiceException>(failed.InnerException).ErrorCode);
            await calls[3];
        }

        Assert.Equal([3], sqsInterceptor.RequestsOf<DeleteMessageBatchRequest>().Select(r => r.Entries.Count));
        Assert.Equal([3], sqsInterceptor.RequestsOf<ChangeMessageVisibilityBatchRequest>().Select(r => r.Entries.Count));
    }

    [Fact]
    public async Task Settle_CancelledWhileWaitingForABatch_IsNotSent()
    {
        // Arrange: the first delete is held, so the next ones wait for it.
        var transport = CreateInterceptedTransport(out var sqsInterceptor);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sqsInterceptor.Intercept = async (request, send) =>
        {
            if (request is DeleteMessageRequest or DeleteMessageBatchRequest)
                await release.Task;

            return await send();
        };

        var lease = TimeSpan.FromSeconds(3);
        var source = MessageSource.Queue(NewName("settle-cancel"));
        DeleteAfterTest(source);
        await using var receiver = await transport.CreateReceiverAsync(source, new ReceiverOptions { LeaseDuration = lease }, TestCancellationToken);
        await transport.SendAsync(MessageDestination.Queue(source.Name), [Message("first", "id-first"), Message("cancelled", "id-cancelled"), Message("waiting", "id-waiting")], TestCancellationToken);
        var messages = (await ReceiveAsync(receiver, 3)).ToDictionary(m => m.MessageId);
        var first = receiver.CompleteAsync(messages["id-first"], TestCancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestCancellationToken);
        var cancelled = receiver.CompleteAsync(messages["id-cancelled"], cancellation.Token);
        var waiting = receiver.CompleteAsync(messages["id-waiting"], TestCancellationToken);

        // Act
        await cancellation.CancelAsync();

        // Assert: the cancelled completion returns at once, is never sent, and its message comes back after its lease.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(TimeSpan.FromSeconds(5), TestCancellationToken));
        release.SetResult();
        await first;
        await waiting;
        Assert.Equal(2, sqsInterceptor.RequestsOf<DeleteMessageRequest>().Count());
        Assert.Empty(sqsInterceptor.RequestsOf<DeleteMessageBatchRequest>());
        Assert.Equal("id-cancelled", (await ReceiveOneAsync(receiver, lease + DeliveryTimeout)).MessageId);
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("abandon")]
    public async Task Settle_WhenTheCallFails_DoesNotKeepTheDelivery(string operation)
    {
        // Arrange
        var transport = CreateInterceptedTransport(out var sqsInterceptor);
        var source = MessageSource.Queue(NewName("settle-forget"));
        DeleteAfterTest(source);
        var receiver = await transport.CreateReceiverAsync(source, new ReceiverOptions { LeaseDuration = TimeSpan.FromSeconds(30) }, TestCancellationToken);
        await transport.SendAsync(MessageDestination.Queue(source.Name), [Message("fails", "id-fails")], TestCancellationToken);
        var message = await ReceiveOneAsync(receiver);
        sqsInterceptor.Intercept = (request, send) => request is DeleteMessageRequest or ChangeMessageVisibilityRequest
            ? Task.FromException<AmazonWebServiceResponse>(new AmazonSQSException("Injected failure.") { ErrorCode = "InternalFailure", ErrorType = ErrorType.Receiver })
            : send();
        await Assert.ThrowsAsync<MessageBusException>(() => Settle(receiver, message, operation));
        sqsInterceptor.Intercept = null;

        // Act
        await receiver.DisposeAsync();

        // Assert: disposing releases the deliveries the receiver still holds; this one was settled, if unsuccessfully.
        Assert.Empty(sqsInterceptor.RequestsOf<ChangeMessageVisibilityBatchRequest>());
    }

    [Fact]
    public async Task ReceiveAsync_SeveralEarlyMessages_HidesThemInBatchCalls()
    {
        // Arrange
        var backlog = new FakeSqsBacklog();
        backlog.AddEarly(25, DateTimeOffset.UtcNow.AddDays(2));
        var (receivers, sqsInterceptor) = await CreateBacklogReceiversAsync(backlog, "hide-batch");

        // Act
        var delivered = await DrainAsync(backlog, receivers);

        // Assert
        Assert.Empty(delivered);
        Assert.Equal(25, backlog.Hides);
        Assert.Empty(sqsInterceptor.RequestsOf<ChangeMessageVisibilityRequest>());
        Assert.Equal([10, 10, 5], sqsInterceptor.RequestsOf<ChangeMessageVisibilityBatchRequest>().Select(r => r.Entries.Count));
    }

    [Fact]
    public async Task ReceiveAsync_SeveralEarlyMessagesWhileManyAreInFlight_SendsThemAgainInBatchCalls()
    {
        Assert.SkipWhen(Fifo, "FIFO queues cannot delay a single message, so an early one is always hidden.");

        // Arrange: four 60 KB messages fill a send batch, since SQS accepts 256 KiB per batch.
        var backlog = new FakeSqsBacklog { OtherInFlight = 100_000 };
        backlog.AddEarly(10, DateTimeOffset.UtcNow.AddMinutes(20), bodySize: 60_000);
        var (receivers, sqsInterceptor) = await CreateBacklogReceiversAsync(backlog, "send-again-batch");

        // Act
        var delivered = await DrainAsync(backlog, receivers);

        // Assert
        Assert.Empty(delivered);
        Assert.Equal(0, backlog.Hides);
        Assert.Equal(0, backlog.InFlight);
        Assert.Equal(10, backlog.Sent.Count);
        Assert.All(backlog.Sent, s => Assert.Equal(900, s.DelaySeconds));
        Assert.Empty(sqsInterceptor.RequestsOf<SendMessageRequest>());
        Assert.Equal([4, 4, 2], sqsInterceptor.RequestsOf<SendMessageBatchRequest>().Select(r => r.Entries.Count));
        Assert.Equal(10, Assert.Single(sqsInterceptor.RequestsOf<DeleteMessageBatchRequest>()).Entries.Count);
    }

    [Fact]
    public async Task ReceiveAsync_ReceiversHidingABurstTogether_StayWithinTheInFlightBudget()
    {
        Assert.SkipWhen(Fifo, "FIFO queues cannot delay a single message, so an early one is always hidden.");

        // Arrange: 45,000 messages are already in flight, so 5,000 more may be hidden before the 50,000 budget is
        // reached. Two receivers, as in two processes, hide a burst of 10,000 together. Each reads the queue's count
        // again after 1,000 hides of its own, so it misses at most the other's last 1,000.
        var backlog = new FakeSqsBacklog { OtherInFlight = 45_000 };
        backlog.AddEarly(10_000, DateTimeOffset.UtcNow.AddDays(2));
        var (receivers, _) = await CreateBacklogReceiversAsync(backlog, "burst", receiverCount: 2);

        // Act
        await DrainAsync(backlog, receivers);

        // Assert: the rest were sent again with a delay, which does not count toward the in-flight quota.
        Assert.InRange(backlog.Hides, 4_990, 7_000);
        Assert.Equal(10_000, backlog.Hides + backlog.Sent.Count);
    }

    [Fact]
    public async Task ReceiveAsync_WhenSqsRefusesItWithOverLimit_ReleasesWhatItHidAndSendsEarlyMessagesAgainUntilTheCountIsReadAgain()
    {
        Assert.SkipWhen(Fifo, "FIFO queues cannot delay a single message, so an early one is always hidden.");

        // Arrange: the receiver hides 20 early messages; then SQS refuses a receive because the queue is over its
        // in-flight quota, which a burst hidden by many receivers can cause.
        var clock = new OffsetTimeProvider();
        var backlog = new FakeSqsBacklog();
        backlog.AddEarly(20, clock.GetUtcNow().AddDays(2));
        var (receivers, _) = await CreateBacklogReceiversAsync(backlog, "over-limit", clock: clock);
        var receiver = receivers[0];
        await DrainAsync(backlog, receiver);
        Assert.Equal(20, backlog.Hides);
        backlog.RefuseReceives = 1;

        // Act
        var received = await receiver.ReceiveAsync(10, TimeSpan.Zero, TestCancellationToken);

        // Assert: the receive returns nothing rather than failing, the hidden messages are released to free their
        // place in the quota, and they come back to be sent again with a delay instead of hidden again.
        Assert.Empty(received);
        Assert.Equal(1, backlog.RefusedReceives);
        Assert.Equal(20, backlog.Released.Count);
        await DrainAsync(backlog, receiver);
        Assert.Equal(20, backlog.Hides);
        Assert.Equal(20, backlog.Sent.Count);
        Assert.Equal(0, backlog.InFlight);

        // A minute later the receiver reads the count again and, the queue no longer crowded, hides early messages again.
        clock.Advance(TimeSpan.FromMinutes(2));
        backlog.AddEarly(10, clock.GetUtcNow().AddDays(2), idPrefix: "later");
        await DrainAsync(backlog, receiver);
        Assert.Equal(30, backlog.Hides);
        Assert.Equal(20, backlog.Sent.Count);
    }

    [Fact]
    public async Task ReceiveAsync_WhenSqsRefusesToHideWithOverLimit_SendsTheEarlyMessagesAgainInstead()
    {
        Assert.SkipWhen(Fifo, "FIFO queues cannot delay a single message, so an early one is always hidden.");

        // Arrange
        var backlog = new FakeSqsBacklog { RefuseHides = true };
        backlog.AddEarly(10, DateTimeOffset.UtcNow.AddDays(2));
        var (receivers, sqsInterceptor) = await CreateBacklogReceiversAsync(backlog, "hide-refused");

        // Act
        await DrainAsync(backlog, receivers);

        // Assert
        Assert.Equal(0, backlog.Hides);
        Assert.Equal(0, backlog.InFlight);
        Assert.Equal(10, backlog.Sent.Count);

        // The receiver stops trying to hide: later early messages are sent again at once.
        int hideAttempts = sqsInterceptor.RequestsOf<ChangeMessageVisibilityBatchRequest>().Count() + sqsInterceptor.RequestsOf<ChangeMessageVisibilityRequest>().Count();
        backlog.AddEarly(10, DateTimeOffset.UtcNow.AddDays(2), idPrefix: "next");
        await DrainAsync(backlog, receivers);
        Assert.Equal(hideAttempts, sqsInterceptor.RequestsOf<ChangeMessageVisibilityBatchRequest>().Count() + sqsInterceptor.RequestsOf<ChangeMessageVisibilityRequest>().Count());
        Assert.Equal(20, backlog.Sent.Count);
    }

    [Fact]
    public async Task Delete_Subscription_UnsubscribesAndRevokesTheTopicButKeepsTheQueue()
    {
        // Arrange
        string topic = NewName("delete-keeps");
        var source = MessageSource.Subscription(topic, "audit");
        await (await CreateReceiverAsync(source)).DisposeAsync();
        using var sqs = CreateSqsClient();
        using var sns = CreateSnsClient();
        string queueUrl = Assert.Single((await sqs.ListQueuesAsync(new ListQueuesRequest { QueueNamePrefix = $"{NamePrefix}{topic}-audit" }, TestCancellationToken)).QueueUrls ?? []);
        Assert.Single(await ListSubscriptionsAsync(sns, topic));

        // Act
        await Transport.DeleteAsync(source, TestCancellationToken);

        // Assert
        Assert.Empty(await ListSubscriptionsAsync(sns, topic));
        var attributes = await sqs.GetQueueAttributesAsync(queueUrl, [QueueAttributeName.Policy], TestCancellationToken);
        Assert.DoesNotContain("FoundatioTopic", attributes.Attributes?.GetValueOrDefault(QueueAttributeName.Policy) ?? String.Empty);

        await using var receiver = await CreateReceiverAsync(source);
        Assert.Single(await ListSubscriptionsAsync(sns, topic));
        await Transport.SendAsync(MessageDestination.Topic(topic), [Message("restored", "id-restored")], TestCancellationToken);
        Assert.Equal("id-restored", (await ReceiveOneAsync(receiver)).MessageId);
    }

    [Fact]
    public async Task Delete_DiscardsMessagesThatWereInvisibleDuringTheDelete()
    {
        // Arrange
        var source = MessageSource.Queue(NewName("delete-hidden"));
        await using (var receiver = await CreateReceiverAsync(source))
        {
            await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("retried", "id-retried")], TestCancellationToken);
            await receiver.AbandonAsync(await ReceiveOneAsync(receiver), TimeSpan.FromSeconds(3), TestCancellationToken);
        }

        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("delayed", "id-delayed") with { DeliverAt = DateTimeOffset.UtcNow.AddSeconds(3) }], TestCancellationToken);

        // Act
        await Transport.DeleteAsync(source, TestCancellationToken);

        // Assert
        await using var recreated = await CreateReceiverAsync(source);
        await AssertNothingReceivedAsync(recreated, TimeSpan.FromSeconds(5));
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("after", "id-after")], TestCancellationToken);
        Assert.Equal("id-after", (await ReceiveOneAsync(recreated)).MessageId);
    }

    [Fact]
    public async Task Delete_WhileAProducerKeepsSending_ReturnsAndKeepsWhatWasSentAfterItStarted()
    {
        // Arrange: the purge sends its marker on its own; everything sent after that is newer than the deletion. Slow
        // receives keep the purge running while the producer sends.
        var transport = CreateInterceptedTransport(out var sqsInterceptor);
        var markerSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sqsInterceptor.Intercept = async (request, send) =>
        {
            if (request is ReceiveMessageRequest)
                await Task.Delay(TimeSpan.FromMilliseconds(300), TestCancellationToken);

            var response = await send();
            if (request is SendMessageRequest)
                markerSent.TrySetResult();

            return response;
        };

        var source = MessageSource.Queue(NewName("delete-busy"));
        await Transport.SendAsync(MessageDestination.Queue(source.Name), Enumerable.Range(0, 25).Select(i => Message("old", $"old-{i}")).ToList(), TestCancellationToken);

        using var stopProducing = CancellationTokenSource.CreateLinkedTokenSource(TestCancellationToken);
        var sentAfterTheMarker = new ConcurrentBag<string>();
        var producer = Task.Run(async () =>
        {
            for (int i = 0; !stopProducing.IsCancellationRequested; i++)
            {
                bool afterMarker = markerSent.Task.IsCompleted;
                await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("new", $"new-{i}")], TestCancellationToken);
                if (afterMarker)
                    sentAfterTheMarker.Add($"new-{i}");

                await Task.Delay(TimeSpan.FromMilliseconds(200), TestCancellationToken);
            }
        }, TestCancellationToken);

        // Act
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestCancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var stopwatch = Stopwatch.StartNew();
        await transport.DeleteAsync(source, timeout.Token);
        var elapsed = stopwatch.Elapsed;
        int sentDuringTheDelete = sentAfterTheMarker.Count;
        await WaitForAsync(() => sentAfterTheMarker.Count >= sentDuringTheDelete + 3);
        await stopProducing.CancelAsync();
        await producer;

        // Assert
        _logger.LogInformation("Deleted in {Elapsed} while {Count} messages were sent after its marker", elapsed, sentDuringTheDelete);
        Assert.True(sentDuringTheDelete > 0, "No message was sent while the delete was running.");
        var expected = sentAfterTheMarker.ToHashSet();
        var received = new HashSet<string>();
        await using var receiver = await CreateReceiverAsync(source);
        var deadline = DateTime.UtcNow + DeliveryTimeout * 3;
        while (!expected.IsSubsetOf(received) && DateTime.UtcNow < deadline)
        {
            foreach (var message in await receiver.ReceiveAsync(10, TimeSpan.FromSeconds(1), TestCancellationToken))
            {
                received.Add(message.MessageId);
                await receiver.CompleteAsync(message, TestCancellationToken);
            }
        }

        Assert.DoesNotContain(received, id => id.StartsWith("old-", StringComparison.Ordinal));
        Assert.Empty(expected.Except(received));
    }

    [Fact]
    public async Task MaxMessageSize_TextBodyWithHeadersThatNeedEscaping_FitsSqsAndCanBeDeadLettered()
    {
        // Arrange: a text body is sent as-is; these headers would grow several times over if escaped as JSON.
        var headers = new Dictionary<string, string>
        {
            [KnownHeaders.MessageType] = "conformance.size",
            ["control"] = new('\u0001', 2000),
            ["quotes"] = new('"', 2000),
            ["emoji"] = String.Concat(Enumerable.Repeat("😀", 1000))
        };
        for (int i = 0; i < 200; i++)
            headers[$"h{i}"] = "v";

        long overhead = MessageSize.Of("at-limit", ReadOnlySpan<byte>.Empty, headers, groupKey: "group");
        string body = "{\"data\":\"" + new string('a', (int)(Transport.Capabilities.MaxMessageSize!.Value - overhead) - 11) + "\"}";
        var outgoing = new OutgoingMessage { MessageId = "at-limit", Body = Encoding.UTF8.GetBytes(body), Headers = headers, GroupKey = "group" };
        Assert.Equal(Transport.Capabilities.MaxMessageSize, MessageSize.Of(outgoing));
        var source = MessageSource.Queue(NewName("size-text"));
        await using var receiver = await CreateReceiverAsync(source);

        // Act
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [outgoing], TestCancellationToken);
        var message = await ReceiveOneAsync(receiver);
        await receiver.DeadLetterAsync(message, new string('r', 5000), TestCancellationToken);

        // Assert
        Assert.Equal(body, Encoding.UTF8.GetString(message.Body.Span));
        Assert.Equal(headers, message.Headers);

        using var sqs = CreateSqsClient();
        string deadLetterUrl = Assert.Single((await sqs.ListQueuesAsync(new ListQueuesRequest { QueueNamePrefix = $"{NamePrefix}{source.Name}-dlq" }, TestCancellationToken)).QueueUrls ?? []);
        var raw = Assert.Single((await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = deadLetterUrl,
            WaitTimeSeconds = 5,
            VisibilityTimeout = 0,
            MessageAttributeNames = ["All"]
        }, TestCancellationToken)).Messages ?? []);
        Assert.InRange(GetSqsSize(raw), Transport.Capabilities.MaxMessageSize.Value, MaxSqsMessageSize);

        await using var deadLetters = await CreateReceiverAsync(MessageSource.DeadLetter(source));
        var deadLetter = await ReceiveOneAsync(deadLetters);
        Assert.Equal(body, Encoding.UTF8.GetString(deadLetter.Body.Span));
        Assert.Equal(new string('r', 1024), deadLetter.Headers[KnownHeaders.DeadLetterReason]);
    }

    [Fact]
    public async Task MaxMessageSize_BinaryBodyPublishedAtTheLimit_RoundTripsThroughSns()
    {
        // Arrange
        var headers = new Dictionary<string, string> { [KnownHeaders.MessageType] = "conformance.size" };
        var body = new byte[(int)(Transport.Capabilities.MaxMessageSize!.Value - MessageSize.Of("at-limit", ReadOnlySpan<byte>.Empty, headers))];
        Random.Shared.NextBytes(body);
        string topic = NewName("size-topic");
        await using var receiver = await CreateReceiverAsync(MessageSource.Subscription(topic, "large"));

        // Act
        await Transport.SendAsync(MessageDestination.Topic(topic), [new OutgoingMessage { MessageId = "at-limit", Body = body, Headers = headers }], TestCancellationToken);

        // Assert
        var message = await ReceiveOneAsync(receiver);
        Assert.True(message.Body.Span.SequenceEqual(body), "The published body at the size limit did not round-trip.");
    }

    [Fact]
    public async Task Send_WhenTheEnvelopeWouldExceedWhatSqsAccepts_ThrowsMessageTooLarge()
    {
        // Arrange: each header costs framing that MaxMessageSize does not count; the transport reserves room for about 1,500.
        var headers = Enumerable.Range(0, 5000).ToDictionary(i => $"h{i}", _ => String.Empty);
        var message = new OutgoingMessage
        {
            MessageId = "many-headers",
            Headers = headers,
            Body = new byte[(int)(Transport.Capabilities.MaxMessageSize!.Value - MessageSize.Of("many-headers", ReadOnlySpan<byte>.Empty, headers))]
        };

        // Act
        var exception = await Assert.ThrowsAsync<MessageTooLargeException>(() => Transport.SendAsync(MessageDestination.Queue(NewName("envelope")), [message], TestCancellationToken));

        // Assert
        Assert.Equal(MaxSqsMessageSize, exception.Limit);
        Assert.True(exception.Size > MaxSqsMessageSize);
    }

    [Fact]
    public async Task DisposingAnInstanceReceiver_DeletesItsQueueAndSubscription()
    {
        // Arrange
        string topic = NewName("instance-cleanup");
        var receiver = await Transport.CreateReceiverAsync(MessageSource.InstanceSubscription(topic, "short-lived"), new ReceiverOptions(), TestCancellationToken);
        using var sqs = CreateSqsClient();
        using var sns = CreateSnsClient();
        Assert.Single(await ListInstanceQueuesAsync(sqs, topic));
        Assert.Single(await ListSubscriptionsAsync(sns, topic));

        // Act
        await receiver.DisposeAsync();

        // Assert
        Assert.Empty(await ListInstanceQueuesAsync(sqs, topic));
        Assert.Empty(await ListSubscriptionsAsync(sns, topic));
    }

    [Fact]
    public async Task InstanceSubscription_OfAStoppedProcess_IsRemovedByTheNextInstanceReceiver()
    {
        // Arrange
        string topic = NewName("crash");
        var options = CreateOptions(instanceSubscriptionTimeout: TimeSpan.FromSeconds(2));
        var crashedSqs = CreateSqsClient();
        var crashedSns = CreateSnsClient();
        await using var crashed = new SqsMessageTransport(crashedSqs, crashedSns, options);
        await crashed.CreateReceiverAsync(MessageSource.InstanceSubscription(topic, "crashed"), new ReceiverOptions(), TestCancellationToken);
        using var sqs = CreateSqsClient();
        using var sns = CreateSnsClient();
        Assert.Single(await ListInstanceQueuesAsync(sqs, topic));

        // The process stops: its heartbeats fail and it never cleans up.
        crashedSqs.Dispose();
        crashedSns.Dispose();
        await Task.Delay(TimeSpan.FromSeconds(3), TestCancellationToken);

        // Act
        var survivor = Track(new SqsMessageTransport(options));
        await using var receiver = await survivor.CreateReceiverAsync(MessageSource.InstanceSubscription(topic, "survivor"), new ReceiverOptions(), TestCancellationToken);

        // Assert
        var queues = await ListInstanceQueuesAsync(sqs, topic);
        Assert.Contains("-i-survivor-", Assert.Single(queues));
        Assert.EndsWith(queues[0][(queues[0].LastIndexOf('/') + 1)..], Assert.Single(await ListSubscriptionsAsync(sns, topic)).Endpoint);

        await survivor.SendAsync(MessageDestination.Topic(topic), [Message("after crash")], TestCancellationToken);
        Assert.Equal("after crash", Encoding.UTF8.GetString((await ReceiveOneAsync(receiver)).Body.Span));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateReceiver_NewSubscriptionQueue_IsCreatedWithThePolicyForItsTopic(bool instance)
    {
        // Arrange: AWS can take a minute to apply a policy set on an existing queue, and SNS drops what it may not deliver.
        var transport = CreateInterceptedTransport(out var sqsInterceptor);
        string topic = NewName("policy-new");
        var source = instance ? MessageSource.InstanceSubscription(topic, "node") : MessageSource.Subscription(topic, "audit");

        // Act
        await using var receiver = await transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken);

        // Assert
        var created = Assert.Single(sqsInterceptor.RequestsOf<CreateQueueRequest>());
        string policy = Assert.Contains(QueueAttributeName.Policy, created.Attributes ?? []);
        Assert.Empty(sqsInterceptor.RequestsOf<SetQueueAttributesRequest>());

        using var sqs = CreateSqsClient();
        using var sns = CreateSnsClient();
        string queueUrl = (await sqs.GetQueueUrlAsync(created.QueueName, TestCancellationToken)).QueueUrl;
        string queueArn = (await sqs.GetQueueAttributesAsync(queueUrl, [QueueAttributeName.QueueArn], TestCancellationToken)).Attributes[QueueAttributeName.QueueArn];
        var statement = Assert.Single(JsonNode.Parse(policy)!["Statement"]!.AsArray())!;
        Assert.Equal(queueArn, statement["Resource"]!.GetValue<string>());
        Assert.Equal(await GetTopicArnAsync(sns, topic), statement["Condition"]!["ArnEquals"]!["aws:SourceArn"]!.GetValue<string>());

        await transport.SendAsync(MessageDestination.Topic(topic), [Message("first", "id-first")], TestCancellationToken);
        Assert.Equal("id-first", (await ReceiveOneAsync(receiver)).MessageId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateReceiver_WhenANewQueueIsNotUsableYet_RetriesUntilItIs(bool instance)
    {
        // Arrange: right after CreateQueue, SQS may still answer that the queue does not exist.
        var transport = CreateInterceptedTransport(out var sqsInterceptor);
        int attributeReads = 0, tagReads = 0;
        sqsInterceptor.Intercept = (request, send) =>
        {
            bool notUsableYet = request switch
            {
                GetQueueAttributesRequest => Interlocked.Increment(ref attributeReads) <= 2,
                ListQueueTagsRequest => Interlocked.Increment(ref tagReads) <= 2,
                _ => false
            };

            return notUsableYet ? Task.FromException<AmazonWebServiceResponse>(new QueueDoesNotExistException("The specified queue does not exist.")) : send();
        };

        string topic = NewName("not-usable");
        var source = instance ? MessageSource.InstanceSubscription(topic, "node") : MessageSource.Subscription(topic, "audit");

        // Act
        await using var receiver = await transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken);

        // Assert
        Assert.True(attributeReads > 2);
        await transport.SendAsync(MessageDestination.Topic(topic), [Message("first", "id-first")], TestCancellationToken);
        Assert.Equal("id-first", (await ReceiveOneAsync(receiver)).MessageId);
    }

    [Fact]
    public async Task Subscribing_KeepsOtherStatementsInTheQueuePolicy()
    {
        // Arrange
        string topic = NewName("policy");
        var source = MessageSource.Subscription(topic, "audit");
        await (await CreateReceiverAsync(source)).DisposeAsync();
        using var sqs = CreateSqsClient();
        string queueUrl = Assert.Single((await sqs.ListQueuesAsync(new ListQueuesRequest { QueueNamePrefix = $"{NamePrefix}{topic}-audit" }, TestCancellationToken)).QueueUrls ?? []);
        string queueArn = (await sqs.GetQueueAttributesAsync(queueUrl, [QueueAttributeName.QueueArn], TestCancellationToken)).Attributes[QueueAttributeName.QueueArn];
        string operatorPolicy = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Sid"] = "OperatorStatement",
                ["Effect"] = "Allow",
                ["Principal"] = new JsonObject { ["AWS"] = "arn:aws:iam::000000000000:root" },
                ["Action"] = "sqs:ReceiveMessage",
                ["Resource"] = queueArn
            })
        }.ToJsonString();
        await sqs.SetQueueAttributesAsync(queueUrl, new Dictionary<string, string> { [QueueAttributeName.Policy] = operatorPolicy }, TestCancellationToken);

        // Act
        var restarted = Track(new SqsMessageTransport(CreateOptions()));
        await using var receiver = await restarted.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken);
        var again = Track(new SqsMessageTransport(CreateOptions()));
        await using var secondReceiver = await again.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken);

        // Assert
        string policy = (await sqs.GetQueueAttributesAsync(queueUrl, [QueueAttributeName.Policy], TestCancellationToken)).Attributes[QueueAttributeName.Policy];
        var sids = JsonNode.Parse(policy)!["Statement"]!.AsArray().Select(s => s!["Sid"]!.GetValue<string>()).ToList();
        Assert.Equal(2, sids.Count);
        Assert.Contains("OperatorStatement", sids);
        Assert.Contains(sids, sid => sid.StartsWith("FoundatioTopic", StringComparison.Ordinal));

        await restarted.SendAsync(MessageDestination.Topic(topic), [Message("audited")], TestCancellationToken);
        Assert.Equal("audited", Encoding.UTF8.GetString((await ReceiveOneAsync(receiver)).Body.Span));
    }

    [Theory]
    [InlineData(MessageSourceKind.Queue)]
    [InlineData(MessageSourceKind.Subscription)]
    public async Task GetStatsAsync_SourceThatDoesNotExist_CountsAsEmptyAndCreatesNothing(MessageSourceKind kind)
    {
        // Arrange
        string name = NewName("stats-missing");
        var source = kind == MessageSourceKind.Queue ? MessageSource.Queue(name) : MessageSource.Subscription(name, "missing");
        var transport = CreateInterceptedTransport(out var sqsInterceptor);

        // Act
        var stats = await transport.GetStatsAsync(source, TestCancellationToken);

        // Assert
        Assert.Equal(new MessageSourceStats { Source = source, Queued = 0, Delayed = Fifo ? null : 0, InFlight = 0, DeadLettered = 0 }, stats);
        Assert.Empty(sqsInterceptor.RequestsOf<CreateQueueRequest>());
        using var sqs = CreateSqsClient();
        Assert.Empty((await sqs.ListQueuesAsync(new ListQueuesRequest { QueueNamePrefix = NamePrefix + name }, TestCancellationToken)).QueueUrls ?? []);
    }

    [Fact]
    public async Task GetStatsAsync_AsMessagesAreReceivedAndSettled_CountsEachState()
    {
        // Arrange
        var source = MessageSource.Queue(NewName("stats-states"));
        await using var receiver = await CreateReceiverAsync(source);
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("a", "a"), Message("b", "b"), Message("c", "c")], TestCancellationToken);
        await AssertStatsAsync(source, queued: 3);

        // Act
        var received = (await ReceiveAsync(receiver, 3)).ToDictionary(m => m.MessageId);
        await AssertStatsAsync(source, inFlight: 3);
        await receiver.CompleteAsync(received["a"], TestCancellationToken);
        await receiver.DeadLetterAsync(received["b"], "stats", TestCancellationToken);
        await receiver.AbandonAsync(received["c"], TimeSpan.FromHours(1), TestCancellationToken);

        // Assert: a retry waiting out its delay is hidden, which SQS counts as in flight.
        await AssertStatsAsync(source, inFlight: 1, deadLettered: 1);
    }

    [Fact]
    public async Task GetStatsAsync_DelayedSend_CountsAsDelayedOnlyOnAStandardQueue()
    {
        // Arrange
        var source = MessageSource.Queue(NewName("stats-delayed"));
        DeleteAfterTest(source);

        // Act
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("later") with { DeliverAt = DateTimeOffset.UtcNow.AddMinutes(10) }], TestCancellationToken);

        // Assert: a FIFO queue cannot delay one message, so it waits visible until a receiver hides it.
        if (Fifo)
            await AssertStatsAsync(source, queued: 1);
        else
            await AssertStatsAsync(source, delayed: 1);
    }

    [Fact]
    public async Task GetStatsAsync_SubscriptionsOfATopic_EachCountTheirOwnCopies()
    {
        // Arrange
        string topic = NewName("stats-topic");
        var first = MessageSource.Subscription(topic, "first");
        var second = MessageSource.Subscription(topic, "second");
        await using var firstReceiver = await CreateReceiverAsync(first);
        await using var secondReceiver = await CreateReceiverAsync(second);

        // Act
        await Transport.SendAsync(MessageDestination.Topic(topic), [Message("one"), Message("two")], TestCancellationToken);
        await Transport.SendAsync(MessageDestination.Subscription(topic, second.Name), [Message("only second")], TestCancellationToken);
        await ReceiveOneAsync(firstReceiver);

        // Assert
        await AssertStatsAsync(first, queued: 1, inFlight: 1);
        await AssertStatsAsync(second, queued: 3);
    }

    [Fact]
    public async Task GetStatsAsync_CalledAgain_OnlyReadsTheAttributesOfTheQueueAndItsDeadLetterQueue()
    {
        // Arrange
        var source = MessageSource.Queue(NewName("stats-cost"));
        await using (var receiver = await CreateReceiverAsync(source))
        {
            await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("poison"), Message("waiting")], TestCancellationToken);
            await receiver.DeadLetterAsync(await ReceiveOneAsync(receiver), "stats", TestCancellationToken);
        }

        var transport = CreateInterceptedTransport(out var sqsInterceptor);
        await transport.GetStatsAsync(source, TestCancellationToken);
        sqsInterceptor.Requests.Clear();

        // Act
        var stats = await transport.GetStatsAsync(source, TestCancellationToken);

        // Assert
        Assert.Equal(2, sqsInterceptor.Requests.Count);
        Assert.All(sqsInterceptor.Requests, request => Assert.IsType<GetQueueAttributesRequest>(request));
        Assert.Equal(1, stats!.DeadLettered);
    }

    [Fact]
    public async Task GetStatsAsync_InstanceSubscription_ReturnsNull()
    {
        // Arrange
        var source = MessageSource.InstanceSubscription(NewName("stats-instance"), "instance");
        await using var receiver = await CreateReceiverAsync(source);

        // Act
        var stats = await Transport.GetStatsAsync(source, TestCancellationToken);

        // Assert
        Assert.Null(stats);
    }

    [Fact]
    public Task GetStatsAsync_DeadLetterSource_ThrowsArgumentException()
    {
        var source = MessageSource.DeadLetter(MessageSource.Queue(NewName("stats-dlq")));

        return Assert.ThrowsAsync<ArgumentException>(() => Transport.GetStatsAsync(source, TestCancellationToken));
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        foreach (var client in _clients)
            client.Dispose();
    }

    /// <summary>A transport whose SQS requests go through an interceptor, so a test can watch and alter them.</summary>
    protected SqsMessageTransport CreateInterceptedTransport(out InterceptingSqsClient sqsInterceptor, TimeProvider? timeProvider = null)
    {
        var sqs = CreateSqsClient();
        var sns = CreateSnsClient();
        _clients.Add(sqs);
        _clients.Add(sns);
        return Track(new SqsMessageTransport(InterceptingSqsClient.Create(sqs, out sqsInterceptor), sns, CreateOptions() with { TimeProvider = timeProvider }));
    }

    protected SqsMessageTransportOptions CreateOptions(TimeSpan? instanceSubscriptionTimeout = null) => new()
    {
        ConnectionString = GetConnectionString(),
        Prefix = NamePrefix,
        Fifo = Fifo,
        LoggerFactory = Log,
        InstanceSubscriptionTimeout = instanceSubscriptionTimeout ?? TimeSpan.FromMinutes(5)
    };

    /// <summary>
    /// Runs <paramref name="concurrency"/> receive loops on one receiver until <paramref name="count"/> messages arrive
    /// or <paramref name="timeout"/> passes. Each loop records what a receive returned before it completes those
    /// messages, so the result lists each group in the order SQS handed it out.
    /// </summary>
    protected async Task<IReadOnlyList<IncomingMessage>> ReceiveConcurrentlyAsync(IMessageReceiver receiver, int concurrency, int count, TimeSpan timeout, int maxMessages = 10)
    {
        var received = new List<IncomingMessage>();
        var deadline = DateTime.UtcNow + timeout;
        await Task.WhenAll(Enumerable.Range(0, concurrency).Select(_ => Task.Run(async () =>
        {
            while (DateTime.UtcNow < deadline)
            {
                lock (received)
                {
                    if (received.Count >= count)
                        return;
                }

                var remaining = deadline - DateTime.UtcNow;
                var messages = await receiver.ReceiveAsync(maxMessages, remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1), TestCancellationToken);
                lock (received)
                    received.AddRange(messages);

                foreach (var message in messages)
                    await receiver.CompleteAsync(message, TestCancellationToken);
            }
        }, TestCancellationToken)));

        return received;
    }

    private Task Settle(IMessageReceiver receiver, IncomingMessage message, string operation) => operation switch
    {
        "complete" => receiver.CompleteAsync(message, TestCancellationToken),
        "renew" => receiver.RenewAsync(message, TestCancellationToken),
        _ => receiver.AbandonAsync(message, TimeSpan.FromMinutes(5), TestCancellationToken)
    };

    private static Amazon.SQS.Model.BatchResultErrorEntry Failure(string id, string code, bool senderFault) => new() { Id = id, Code = code, Message = code, SenderFault = senderFault };

    /// <summary>Opens receivers of a new queue whose messages and in-flight count <paramref name="backlog"/> plays.</summary>
    private async Task<(IMessageReceiver[] Receivers, InterceptingSqsClient Interceptor)> CreateBacklogReceiversAsync(FakeSqsBacklog backlog, string name, int receiverCount = 1, TimeProvider? clock = null)
    {
        var transport = CreateInterceptedTransport(out var sqsInterceptor, clock);
        var source = MessageSource.Queue(NewName(name));
        DeleteAfterTest(source);
        var receivers = new IMessageReceiver[receiverCount];
        for (int i = 0; i < receiverCount; i++)
            receivers[i] = await transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken);

        sqsInterceptor.Requests.Clear();
        sqsInterceptor.Intercept = backlog.InterceptAsync;
        return (receivers, sqsInterceptor);
    }

    /// <summary>Polls with every receiver, one receive each at a time, until the backlog has nothing visible, and returns what they delivered.</summary>
    private async Task<List<IncomingMessage>> DrainAsync(FakeSqsBacklog backlog, params IMessageReceiver[] receivers)
    {
        var delivered = new List<IncomingMessage>();
        var deadline = DateTime.UtcNow + DeliveryTimeout * 3;
        while (backlog.Visible > 0)
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"{backlog.Visible} messages were still visible.");

            foreach (var messages in await Task.WhenAll(receivers.Select(r => r.ReceiveAsync(10, TimeSpan.Zero, TestCancellationToken))))
                delivered.AddRange(messages);
        }

        return delivered;
    }

    /// <summary>Sends a message straight to SQS, as another producer would.</summary>
    protected Task<SendMessageResponse> RawSendAsync(IAmazonSQS sqs, SendMessageRequest request)
    {
        if (Fifo)
        {
            request.MessageGroupId ??= Guid.NewGuid().ToString("N");
            request.MessageDeduplicationId ??= Guid.NewGuid().ToString("N");
        }

        return sqs.SendMessageAsync(request, TestCancellationToken);
    }

    // Polls until the counts match: SQS's approximate counts can lag sends and settlements. FIFO queues do not delay
    // single messages, so the transport does not report a delayed count for them.
    private async Task AssertStatsAsync(MessageSource source, long queued = 0, long delayed = 0, long inFlight = 0, long deadLettered = 0)
    {
        var expected = new MessageSourceStats { Source = source, Queued = queued, Delayed = Fifo ? null : delayed, InFlight = inFlight, DeadLettered = deadLettered };
        var deadline = DateTime.UtcNow + DeliveryTimeout;
        var stats = await Transport.GetStatsAsync(source, TestCancellationToken);
        while (stats != expected && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, TestCancellationToken);
            stats = await Transport.GetStatsAsync(source, TestCancellationToken);
        }

        Assert.Equal(expected, stats);
    }

    /// <summary>What SQS counts against its size limit: the body plus every attribute's name, data type and value.</summary>
    private static long GetSqsSize(Amazon.SQS.Model.Message message) => Encoding.UTF8.GetByteCount(message.Body) + (message.MessageAttributes ?? new()).Sum(a =>
        Encoding.UTF8.GetByteCount(a.Key) + Encoding.UTF8.GetByteCount(a.Value.DataType)
        + (a.Value.BinaryValue is { } binary ? binary.Length : Encoding.UTF8.GetByteCount(a.Value.StringValue ?? String.Empty)));

    private static string GetConnectionString()
    {
        string? connectionString = _configuration.GetConnectionString("SQSMessageBus");
        Assert.SkipWhen(String.IsNullOrEmpty(connectionString), "ConnectionStrings:SQSMessageBus is not configured.");
        return connectionString;
    }

    protected static AmazonSQSClient CreateSqsClient()
    {
        var builder = new SqsMessageTransportConnectionStringBuilder(GetConnectionString());
        var config = new AmazonSQSConfig();
        if (!String.IsNullOrEmpty(builder.ServiceUrl))
        {
            config.ServiceURL = builder.ServiceUrl;
            config.AuthenticationRegion = builder.Region ?? "us-east-1";
        }
        else if (builder.GetRegion() is { } region)
        {
            config.RegionEndpoint = region;
        }

        return builder.GetCredentials() is { } credentials ? new AmazonSQSClient(credentials, config) : new AmazonSQSClient(config);
    }

    protected static AmazonSimpleNotificationServiceClient CreateSnsClient()
    {
        var builder = new SqsMessageTransportConnectionStringBuilder(GetConnectionString());
        var config = new AmazonSimpleNotificationServiceConfig();
        if (!String.IsNullOrEmpty(builder.ServiceUrl))
        {
            config.ServiceURL = builder.ServiceUrl;
            config.AuthenticationRegion = builder.Region ?? "us-east-1";
        }
        else if (builder.GetRegion() is { } region)
        {
            config.RegionEndpoint = region;
        }

        return builder.GetCredentials() is { } credentials ? new AmazonSimpleNotificationServiceClient(credentials, config) : new AmazonSimpleNotificationServiceClient(config);
    }

    private async Task<List<string>> ListInstanceQueuesAsync(IAmazonSQS sqs, string topic)
    {
        var response = await sqs.ListQueuesAsync(new ListQueuesRequest { QueueNamePrefix = NamePrefix + topic, MaxResults = 1000 }, TestCancellationToken);
        return (response.QueueUrls ?? []).Where(url => Regex.IsMatch(url, "-i-[^/]*$")).ToList();
    }

    protected async Task<List<Subscription>> ListSubscriptionsAsync(IAmazonSimpleNotificationService sns, string topic)
    {
        string topicArn = await GetTopicArnAsync(sns, topic);
        return (await sns.ListSubscriptionsByTopicAsync(topicArn, TestCancellationToken)).Subscriptions ?? [];
    }

    protected async Task<string> GetTopicArnAsync(IAmazonSimpleNotificationService sns, string topic)
    {
        var request = new CreateTopicRequest { Name = NamePrefix + topic + Suffix };
        if (Fifo)
            request.Attributes = new Dictionary<string, string> { ["FifoTopic"] = "true" };

        return (await sns.CreateTopicAsync(request, TestCancellationToken)).TopicArn;
    }
}
