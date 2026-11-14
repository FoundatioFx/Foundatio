using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Azure.Core.Amqp;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;
using Foundatio.Tests.Messaging;
using Foundatio.Tests.Utility;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Foundatio.AzureServiceBus.Tests.Messaging;

/// <summary>
/// Runs the conformance suite against queues, topics and subscriptions without sessions or duplicate detection, plus what
/// only the Service Bus transport does. Each test gets its own name prefix, and its entities, topics included, are
/// deleted when it ends, so the emulator's limit of 50 queues and topics is never reached.
/// </summary>
public class AzureServiceBusMessageTransportTests(ITestOutputHelper output) : MessageTransportTestsBase(output)
{
    /// <summary>The Service Bus emulator accepts at most 10.</summary>
    private const int EmulatorMaxDeliveryCount = 10;

    private readonly string _prefix = $"ft{Guid.NewGuid():N}"[..10] + "-";
    private ServiceBusClient? _client;

    /// <summary>Runs every test against session-enabled queues and subscriptions when true.</summary>
    protected virtual bool RequiresSession => false;

    /// <summary>Runs every test against queues and topics with duplicate detection when true.</summary>
    protected virtual bool RequiresDuplicateDetection => false;

    /// <summary>The lock duration of every entity the tests create, so lease tests wait for it.</summary>
    protected override TimeSpan ShortLease => TimeSpan.FromSeconds(5);

    protected override IMessageTransport CreateTransport() => new AzureServiceBusMessageTransport(CreateOptions());

    /// <summary>A client of its own, as another producer or consumer of the namespace would use.</summary>
    protected ServiceBusClient Client => _client ??= new ServiceBusClient(GetConnectionString());

    protected static ServiceBusAdministrationClient CreateAdministrationClient() =>
        new(AzureServiceBusMessageTransport.GetAdministrationConnectionString(GetConnectionString()));

    protected string Prefix => _prefix;

    protected AzureServiceBusMessageTransportOptions CreateOptions() => new()
    {
        ConnectionString = GetConnectionString(),
        Prefix = _prefix,
        RequiresSession = RequiresSession,
        RequiresDuplicateDetection = RequiresDuplicateDetection,
        DuplicateDetectionWindow = TimeSpan.FromMinutes(1),
        LockDuration = ShortLease,
        MaxDeliveryCount = EmulatorMaxDeliveryCount,
        LoggerFactory = Log
    };

    [Fact]
    public async Task Names_LongOrEdgeCaseNames_MapToLegalEntitiesThatDoNotCollide()
    {
        // Arrange
        string name = NewName("collide");
        string tooLong = name + new string('x', 300);
        string[] queues = [$"{name}.a", $"{name}_a", tooLong + "1", tooLong + "2", $"-{name}-", name];
        var receivers = new List<IMessageReceiver>();
        foreach (string queue in queues)
            receivers.Add(await CreateReceiverAsync(MessageSource.Queue(queue)));

        // A topic may have the name of a queue; subscription names longer than 50 characters are shortened and hashed.
        var subscriptions = new[] { MessageSource.Subscription(name, new string('s', 60) + "1"), MessageSource.Subscription(name, new string('s', 60) + "2") };
        foreach (var subscription in subscriptions)
            receivers.Add(await CreateReceiverAsync(subscription));

        try
        {
            // Act
            foreach (string queue in queues)
                await Transport.SendAsync(MessageDestination.Queue(queue), [Message(queue)], TestCancellationToken);
            foreach (var subscription in subscriptions)
                await Transport.SendAsync(MessageDestination.For(subscription), [Message(subscription.Name)], TestCancellationToken);

            // Assert
            string[] expected = [.. queues, .. subscriptions.Select(s => s.Name)];
            for (int i = 0; i < receivers.Count; i++)
            {
                var message = await ReceiveOneAsync(receivers[i]);
                Assert.Equal(expected[i], Encoding.UTF8.GetString(message.Body.Span));
                await receivers[i].CompleteAsync(message, TestCancellationToken);
                await AssertNothingReceivedAsync(receivers[i], TimeSpan.FromMilliseconds(500));
            }

            var admin = CreateAdministrationClient();
            var entityNames = new List<string>();
            await foreach (var queue in admin.GetQueuesAsync(TestCancellationToken))
            {
                if (queue.Name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                    entityNames.Add(queue.Name);
            }

            Assert.Equal(queues.Length, entityNames.Count);
            string topic = new AzureServiceBusMessageTransportNames(Prefix).Topic(name);
            await foreach (var subscription in admin.GetSubscriptionsAsync(topic, TestCancellationToken))
            {
                Assert.InRange(subscription.SubscriptionName.Length, 1, 50);
                entityNames.Add(subscription.SubscriptionName);
            }

            Assert.Equal(queues.Length + subscriptions.Length, entityNames.Count);
            Assert.All(entityNames, n => Assert.Matches("^[A-Za-z0-9]([A-Za-z0-9._-]{0,258}[A-Za-z0-9])?$", n));
        }
        finally
        {
            foreach (var receiver in receivers)
                await receiver.DisposeAsync();
        }
    }

    [Fact]
    public async Task Receive_MessageFromAnotherProducer_UsesItsBodyIdAndProperties()
    {
        // Arrange
        var source = MessageSource.Queue(NewName("foreign"));
        await using var receiver = await CreateReceiverAsync(source);
        await using var sender = Client.CreateSender(new AzureServiceBusMessageTransportNames(Prefix).Queue(source.Name));
        var data = new ServiceBusMessage("{\"orderId\":42}") { MessageId = "foreign-data", SessionId = RequiresSession ? "foreign-group" : null };
        data.ApplicationProperties["count"] = 42;
        data.ApplicationProperties["flag"] = true;
        data.ApplicationProperties["text"] = "héllo";
        data.ApplicationProperties["when"] = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var value = new ServiceBusMessage(new AmqpAnnotatedMessage(AmqpMessageBody.FromValue("an AMQP value"))) { MessageId = "foreign-value", SessionId = RequiresSession ? "foreign-value" : null };

        // Act
        await sender.SendMessagesAsync([data, value], TestCancellationToken);

        // Assert
        var messages = (await ReceiveAsync(receiver, 2)).ToDictionary(m => m.MessageId);
        var received = messages["foreign-data"];
        Assert.Equal("{\"orderId\":42}", Encoding.UTF8.GetString(received.Body.Span));
        Assert.Equal("42", received.Headers["count"]);
        Assert.Equal("true", received.Headers["flag"]);
        Assert.Equal("héllo", received.Headers["text"]);
        Assert.Equal("2026-01-02T03:04:05.0000000+00:00", received.Headers["when"]);
        Assert.Equal(RequiresSession ? "foreign-group" : null, received.GroupKey);
        Assert.Equal(1, received.DeliveryCount);
        Assert.NotNull(received.EnqueuedAt);
        Assert.NotNull(received.LeaseExpiresAt);
        Assert.Equal("an AMQP value", Encoding.UTF8.GetString(messages["foreign-value"].Body.Span));
        foreach (var message in messages.Values)
            await receiver.CompleteAsync(message, TestCancellationToken);
    }

    [Fact]
    public async Task Send_HeadersOverTheServiceBusHeaderLimit_TravelInTheBodyAndRoundTrip()
    {
        // Arrange
        var source = MessageSource.Queue(NewName("big-headers"));
        await using var receiver = await CreateReceiverAsync(source);
        var headers = Enumerable.Range(0, 100).ToDictionary(i => $"header-{i}", i => new string((char)('a' + i % 26), 1000) + "✓");
        var body = new byte[1000];
        Random.Shared.NextBytes(body);

        // Act
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [new OutgoingMessage { MessageId = "big-headers", Body = body, Headers = headers }], TestCancellationToken);

        // Assert
        var message = await ReceiveOneAsync(receiver);
        Assert.True(message.Body.Span.SequenceEqual(body), "The body changed.");
        Assert.Equal(headers, message.Headers);
        await receiver.CompleteAsync(message, TestCancellationToken);
    }

    [Fact]
    public Task Send_HeaderWithTheReservedPrefix_ThrowsArgumentException()
    {
        var source = MessageSource.Queue(NewName("reserved"));
        DeleteAfterTest(source);

        return Assert.ThrowsAsync<ArgumentException>(() => Transport.SendAsync(MessageDestination.Queue(source.Name),
            [Message("reserved", headers: new Dictionary<string, string> { [AzureServiceBusMessageTransportCodec.TargetProperty] = "elsewhere" })], TestCancellationToken));
    }

    [Fact]
    public async Task CreateReceiver_InstanceSubscription_IsCreatedToBeRemovedWhenIdleAndDeletedOnDispose()
    {
        // Arrange
        string topic = NewName("instance");
        string topicPath = new AzureServiceBusMessageTransportNames(Prefix).Topic(topic);
        var admin = CreateAdministrationClient();

        // Act
        var receiver = await CreateReceiverAsync(MessageSource.InstanceSubscription(topic, "my-service"));
        var created = new List<SubscriptionProperties>();
        await foreach (var subscription in admin.GetSubscriptionsAsync(topicPath, TestCancellationToken))
            created.Add(subscription);
        var rules = created.Count == 1 ? await ListRulesAsync(admin, topicPath, created[0].SubscriptionName) : [];
        await receiver.DisposeAsync();

        // Assert
        var instance = Assert.Single(created);
        Assert.StartsWith("my-service-", instance.SubscriptionName);
        // The emulator keeps subscriptions idle forever and reports so.
        if (!AzureServiceBusMessageTransport.IsEmulatorConnectionString(GetConnectionString()))
            Assert.Equal(TimeSpan.FromMinutes(5), instance.AutoDeleteOnIdle);
        Assert.Equal(RequiresSession, instance.RequiresSession);
        var rule = Assert.Single(rules);
        Assert.Equal(AzureServiceBusMessageTransportEntities.RuleName, rule.Name);
        await Assert.ThrowsAsync<ServiceBusException>(() => admin.GetSubscriptionAsync(topicPath, instance.SubscriptionName, TestCancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateReceiver_SubscriptionWithTheDefaultRule_EndsWithOnlyTheTransportsRule(bool replacementStoppedHalfway)
    {
        // Arrange
        string topic = NewName("default-rule");
        var names = new AzureServiceBusMessageTransportNames(Prefix);
        string topicPath = names.Topic(topic);
        var admin = CreateAdministrationClient();
        await admin.CreateTopicAsync(topicPath, TestCancellationToken);
        await admin.CreateSubscriptionAsync(new CreateSubscriptionOptions(topicPath, "existing") { RequiresSession = RequiresSession, MaxDeliveryCount = EmulatorMaxDeliveryCount }, TestCancellationToken);
        if (replacementStoppedHalfway)
        {
            await admin.CreateRuleAsync(topicPath, "existing", new CreateRuleOptions(AzureServiceBusMessageTransportEntities.RuleName,
                new SqlRuleFilter(AzureServiceBusMessageTransportEntities.GetRuleFilter("existing"))), TestCancellationToken);
        }

        // Act
        await using var existing = await CreateReceiverAsync(MessageSource.Subscription(topic, "existing"));
        await using var other = await CreateReceiverAsync(MessageSource.Subscription(topic, "other"));
        await Transport.SendAsync(MessageDestination.Subscription(topic, "other"), [Message("not for existing", "id-other")], TestCancellationToken);
        await Transport.SendAsync(MessageDestination.Topic(topic), [Message("for everyone", "id-everyone")], TestCancellationToken);

        // Assert
        var rule = Assert.Single(await ListRulesAsync(admin, topicPath, "existing"));
        Assert.Equal(AzureServiceBusMessageTransportEntities.RuleName, rule.Name);
        await AssertOnlyReceivedAsync(existing, "id-everyone");
    }

    [Fact]
    public async Task Send_ToAQueueAnotherProcessDeleted_CreatesItAgainAndDeliversTheMessage()
    {
        // Arrange
        var source = MessageSource.Queue(NewName("deleted-send"));
        await (await CreateReceiverAsync(source)).DisposeAsync();
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("before", "id-before")], TestCancellationToken);
        await CreateAdministrationClient().DeleteQueueAsync(new AzureServiceBusMessageTransportNames(Prefix).Queue(source.Name), TestCancellationToken);

        // Act
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("after", "id-after")], TestCancellationToken);

        // Assert
        await using var receiver = await CreateReceiverAsync(source);
        await AssertOnlyReceivedAsync(receiver, "id-after");
    }

    [Fact]
    public async Task Receive_FromAQueueAnotherProcessDeleted_FaultsAndTheReplacementCreatesItAgain()
    {
        // Arrange
        var source = MessageSource.Queue(NewName("deleted-receive"));
        var receiver = await CreateReceiverAsync(source);
        await CreateAdministrationClient().DeleteQueueAsync(new AzureServiceBusMessageTransportNames(Prefix).Queue(source.Name), TestCancellationToken);
        await Assert.ThrowsAsync<MessageReceiverFaultedException>(() => receiver.ReceiveAsync(1, TimeSpan.FromSeconds(5), TestCancellationToken));
        await receiver.DisposeAsync();

        // Act
        await using var replacement = await CreateReceiverAsync(source);
        var received = await replacement.ReceiveAsync(1, TimeSpan.FromSeconds(1), TestCancellationToken);

        // Assert
        Assert.Empty(received);
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("after", "id-after")], TestCancellationToken);
        await AssertOnlyReceivedAsync(replacement, "id-after");
    }

    [Fact]
    public async Task Abandon_WithDelayOnASubscription_RedeliversToThatSubscriptionOnlyWithTheDeliveryCount()
    {
        // Arrange
        string topic = NewName("sub-retry");
        await using var receiver = await CreateReceiverAsync(MessageSource.Subscription(topic, "retried"));
        await using var other = await CreateReceiverAsync(MessageSource.Subscription(topic, "other"));
        await Transport.SendAsync(MessageDestination.Topic(topic), [Message("retry me", "id-retry")], TestCancellationToken);
        await other.CompleteAsync(await ReceiveOneAsync(other), TestCancellationToken);

        // Act
        var first = await ReceiveOneAsync(receiver);
        await receiver.AbandonAsync(first, ShortDelay, TestCancellationToken);
        var second = await ReceiveOneAsync(receiver, ShortDelay + DeliveryTimeout);
        await receiver.AbandonAsync(second, ShortDelay, TestCancellationToken);
        var third = await ReceiveOneAsync(receiver, ShortDelay + DeliveryTimeout);

        // Assert
        Assert.Equal(["id-retry", "id-retry"], new[] { second.MessageId, third.MessageId });
        Assert.Equal([2, 3], new[] { second.DeliveryCount, third.DeliveryCount });
        Assert.Equal("retry me", Encoding.UTF8.GetString(third.Body.Span));
        await receiver.CompleteAsync(third, TestCancellationToken);
        await AssertNothingReceivedAsync(other, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task MaxMessageSize_MessageAtTheLimitRetriedWithADelay_IsDeliveredAgain()
    {
        // Arrange
        var headers = new Dictionary<string, string> { [KnownHeaders.MessageType] = "size.retry" };
        for (int i = 0; i < 100; i++)
            headers[$"h{i}"] = "v";
        string? deduplicationId = Transport.Capabilities.Deduplication ? "retry-at-limit" : null;
        var body = new byte[(int)(Transport.Capabilities.MaxMessageSize!.Value - MessageSize.Of("retry-at-limit", ReadOnlySpan<byte>.Empty, headers, deduplicationId: deduplicationId))];
        Random.Shared.NextBytes(body);
        var source = MessageSource.Queue(NewName("size-retry"));
        await using var receiver = await CreateReceiverAsync(source);
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [new OutgoingMessage { MessageId = "retry-at-limit", Body = body, Headers = headers, DeduplicationId = deduplicationId }], TestCancellationToken);

        // Act
        var first = await ReceiveOneAsync(receiver);
        await receiver.AbandonAsync(first, ShortDelay, TestCancellationToken);
        var second = await ReceiveOneAsync(receiver, ShortDelay + DeliveryTimeout);

        // Assert
        Assert.Equal("retry-at-limit", second.MessageId);
        Assert.Equal(2, second.DeliveryCount);
        Assert.True(second.Body.Span.SequenceEqual(body), "The retried body changed.");
        Assert.Equal(headers, second.Headers);
        await receiver.CompleteAsync(second, TestCancellationToken);
    }

    [Fact]
    public async Task DeadLetter_ByServiceBusAfterTheMaxDeliveryCount_IsReceivedWithItsReason()
    {
        // Arrange
        var source = MessageSource.Queue(NewName("max-delivery"));
        await using var receiver = await CreateReceiverAsync(source);
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("poison", "id-max-delivery")], TestCancellationToken);

        // Act
        for (int i = 0; i < EmulatorMaxDeliveryCount; i++)
            await receiver.AbandonAsync(await ReceiveOneAsync(receiver), TimeSpan.Zero, TestCancellationToken);

        // Assert
        await using var deadLetters = await CreateReceiverAsync(MessageSource.DeadLetter(source));
        var deadLetter = await ReceiveOneAsync(deadLetters);
        Assert.Equal("id-max-delivery", deadLetter.MessageId);
        Assert.StartsWith("MaxDeliveryCountExceeded", deadLetter.Headers[KnownHeaders.DeadLetterReason]);
        Assert.DoesNotContain("DeadLetterReason", deadLetter.Headers.Keys);
        await AssertNothingReceivedAsync(receiver, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task CreateEntitiesOff_UsesExistingEntitiesAndRefusesWhatNeedsManageRights()
    {
        // Arrange
        var existing = MessageSource.Queue(NewName("existing"));
        var missing = MessageSource.Queue(NewName("missing"));
        await (await CreateReceiverAsync(existing)).DisposeAsync();
        await using var limited = new AzureServiceBusMessageTransport(CreateOptions() with { CreateEntities = false });

        // Act
        await limited.SendAsync(MessageDestination.Queue(existing.Name), [Message("existing", "id-existing")], TestCancellationToken);
        await using var receiver = await limited.CreateReceiverAsync(existing, new ReceiverOptions(), TestCancellationToken);
        var message = await ReceiveOneAsync(receiver);
        await receiver.CompleteAsync(message, TestCancellationToken);
        await using var missingReceiver = await limited.CreateReceiverAsync(missing, new ReceiverOptions(), TestCancellationToken);

        // Assert
        Assert.Equal("id-existing", message.MessageId);
        await Assert.ThrowsAsync<MessageBusException>(() => limited.SendAsync(MessageDestination.Queue(missing.Name), [Message("lost")], TestCancellationToken));
        await Assert.ThrowsAsync<MessageReceiverFaultedException>(() => missingReceiver.ReceiveAsync(1, TimeSpan.FromSeconds(1), TestCancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => limited.CreateReceiverAsync(MessageSource.InstanceSubscription(NewName("instances")), new ReceiverOptions(), TestCancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => limited.DeleteAsync(existing, TestCancellationToken));
        await limited.DeleteAsync(MessageSource.DeadLetter(existing), TestCancellationToken);
    }

    [Fact]
    public async Task CreateReceiver_QueueCreatedForTheOtherSessionMode_ThrowsMessageBusException()
    {
        // Arrange
        var source = MessageSource.Queue(NewName("other-mode"));
        await using var otherMode = new AzureServiceBusMessageTransport(CreateOptions() with { RequiresSession = !RequiresSession });
        await (await otherMode.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken)).DisposeAsync();
        DeleteAfterTest(source);

        // Act
        var exception = await Assert.ThrowsAsync<MessageBusException>(() => Transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken));

        // Assert
        Assert.Contains("session", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetStatsAsync_OnTheEmulator_ReturnsNull()
    {
        Assert.SkipUnless(AzureServiceBusMessageTransport.IsEmulatorConnectionString(GetConnectionString()), "Only the emulator reports no counts.");
        var source = MessageSource.Queue(NewName("stats-emulator"));
        DeleteAfterTest(source);

        Assert.Null(await Transport.GetStatsAsync(source, TestCancellationToken));
    }

    protected async Task<List<RuleProperties>> ListRulesAsync(ServiceBusAdministrationClient admin, string topic, string subscription)
    {
        var rules = new List<RuleProperties>();
        await foreach (var rule in admin.GetRulesAsync(topic, subscription, TestCancellationToken))
            rules.Add(rule);

        return rules;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (_client is not null)
            await _client.DisposeAsync();

        await DeleteEntitiesAsync();
    }

    /// <summary>Deletes every queue and topic with this test's prefix; the emulator lowercases names.</summary>
    private async Task DeleteEntitiesAsync()
    {
        try
        {
            var admin = CreateAdministrationClient();
            var queues = new List<string>();
            await foreach (var queue in admin.GetQueuesAsync())
            {
                if (queue.Name.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase))
                    queues.Add(queue.Name);
            }

            var topics = new List<string>();
            await foreach (var topic in admin.GetTopicsAsync())
            {
                if (topic.Name.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase))
                    topics.Add(topic.Name);
            }

            foreach (string queue in queues)
                await admin.DeleteQueueAsync(queue);
            foreach (string topic in topics)
                await admin.DeleteTopicAsync(topic);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete the entities of prefix {Prefix}", _prefix);
        }
    }

    protected static string GetConnectionString()
    {
        string? connectionString = Configuration.GetConnectionString("AzureServiceBusConnectionString");
        Assert.SkipWhen(String.IsNullOrEmpty(connectionString), "ConnectionStrings:AzureServiceBusConnectionString is not configured.");
        return connectionString;
    }
}
