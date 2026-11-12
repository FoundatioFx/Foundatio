using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;
using Foundatio.Tests.Messaging;
using Microsoft.Extensions.Configuration;
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
        NamePrefix = NamePrefix,
        Fifo = Fifo,
        LoggerFactory = Log,
        InstanceSubscriptionTimeout = instanceSubscriptionTimeout ?? TimeSpan.FromMinutes(5)
    };

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
