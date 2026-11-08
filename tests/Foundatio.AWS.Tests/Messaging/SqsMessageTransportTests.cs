using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using Foundatio.Messaging.Transport;
using Foundatio.Tests.Messaging;
using Microsoft.Extensions.Configuration;
using Xunit;
using SqsMessageAttributeValue = Amazon.SQS.Model.MessageAttributeValue;

namespace Foundatio.AWS.Tests.Messaging;

public class SqsMessageTransportTests(ITestOutputHelper output) : MessageTransportTestsBase(output)
{
    private const string NamePrefix = "ft-";

    private static readonly IConfiguration _configuration = new ConfigurationBuilder()
        .SetBasePath(Directory.GetCurrentDirectory())
        .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
        .AddEnvironmentVariables()
        .Build();

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
            Assert.All(queueNames, n => Assert.Matches("^[A-Za-z0-9_-]{1,80}$", n));
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
        string queueUrl = (await sqs.GetQueueUrlAsync(NamePrefix + source.Name, TestCancellationToken)).QueueUrl;
        string[] bodies = ["{\"orderId\":42,\"note\":\"héllo\"}", "aGVsbG8=", "-"];

        // Act
        var sent = new Dictionary<string, string>();
        foreach (string body in bodies)
            sent[(await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = queueUrl, MessageBody = body }, TestCancellationToken)).MessageId] = body;

        var notAnEnvelope = await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "{\"e\":\"base64\"}",
            MessageAttributes = new Dictionary<string, SqsMessageAttributeValue> { ["foundatio"] = new() { DataType = "String", StringValue = "not json" } }
        }, TestCancellationToken);
        sent[notAnEnvelope.MessageId] = "{\"e\":\"base64\"}";

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
    public async Task Publish_WithDeliverAt_IsHeldUntilDueAndKeepsItsDeliveryCount()
    {
        // Arrange
        string topic = NewName("topic-delay");
        await using var receiver = await CreateReceiverAsync(MessageSource.Subscription(topic, "later"));
        var stopwatch = Stopwatch.StartNew();

        // Act
        await Transport.SendAsync(MessageDestination.Topic(topic), [Message("held") with { DeliverAt = DateTimeOffset.UtcNow.Add(ShortDelay) }], TestCancellationToken);
        var message = await ReceiveOneAsync(receiver, ShortDelay + DeliveryTimeout);

        // Assert
        Assert.True(stopwatch.Elapsed >= ShortDelay * 0.75, $"Delivered after {stopwatch.Elapsed}, expected at least {ShortDelay}.");
        Assert.Equal("held", Encoding.UTF8.GetString(message.Body.Span));
        Assert.Equal(1, message.DeliveryCount);
        await receiver.CompleteAsync(message, TestCancellationToken);
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

    private SqsMessageTransportOptions CreateOptions(TimeSpan? instanceSubscriptionTimeout = null) => new()
    {
        ConnectionString = GetConnectionString(),
        NamePrefix = NamePrefix,
        LoggerFactory = Log,
        InstanceSubscriptionTimeout = instanceSubscriptionTimeout ?? TimeSpan.FromMinutes(5)
    };

    private static string GetConnectionString()
    {
        string? connectionString = _configuration.GetConnectionString("SQSMessageBus");
        Assert.SkipWhen(String.IsNullOrEmpty(connectionString), "ConnectionStrings:SQSMessageBus is not configured.");
        return connectionString;
    }

    private static AmazonSQSClient CreateSqsClient()
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

    private static AmazonSimpleNotificationServiceClient CreateSnsClient()
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

    private async Task<List<Subscription>> ListSubscriptionsAsync(IAmazonSimpleNotificationService sns, string topic)
    {
        string topicArn = (await sns.CreateTopicAsync(NamePrefix + topic, TestCancellationToken)).TopicArn;
        return (await sns.ListSubscriptionsByTopicAsync(topicArn, TestCancellationToken)).Subscriptions ?? [];
    }
}
