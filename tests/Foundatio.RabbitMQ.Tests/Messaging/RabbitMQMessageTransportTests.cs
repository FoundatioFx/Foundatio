using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;
using Foundatio.Tests.Messaging;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using Xunit;

namespace Foundatio.RabbitMQ.Tests.Messaging;

public class RabbitMQMessageTransportTests(RabbitMQTransportFixture fixture, ITestOutputHelper output) : MessageTransportTestsBase(output)
{
    internal const string Prefix = "foundatio-tests";

    private readonly List<IConnection> _connections = [];

    protected RabbitMQTransportFixture Fixture { get; } = fixture;

    /// <summary>Runs every test with the transport's delay queues when true, and without delay support when false.</summary>
    protected virtual bool DelayedDelivery => true;

    protected override IMessageTransport CreateTransport() => new RabbitMQMessageTransport(CreateOptions());

    protected RabbitMQMessageTransportOptions CreateOptions() => new()
    {
        ConnectionString = Fixture.ConnectionString,
        Prefix = Prefix,
        DelayedDelivery = DelayedDelivery,
        LoggerFactory = Log
    };

    private RabbitMQMessageTransport RabbitTransport => (RabbitMQMessageTransport)Transport;

    [Fact]
    public async Task InstanceSubscription_WhenItsReceiverIsDisposed_IsDeleted()
    {
        // Arrange
        var receiver = (RabbitMQMessageReceiver)await CreateReceiverAsync(MessageSource.InstanceSubscription(NewName("instance-dispose"), "probe"));
        string queue = receiver.QueueName;
        Assert.True(await QueueExistsAsync(queue));

        // Act
        await receiver.DisposeAsync();

        // Assert
        Assert.False(await QueueExistsAsync(queue));
    }

    [Fact]
    public async Task InstanceSubscription_WhenItsConnectionIsLost_IsRemovedByTheBroker()
    {
        // Arrange
        var transport = Track(new RabbitMQMessageTransport(CreateOptions()));
        var receiver = (RabbitMQMessageReceiver)await transport.CreateReceiverAsync(MessageSource.InstanceSubscription(NewName("instance-crash"), "probe"), new ReceiverOptions(), TestCancellationToken);
        string queue = receiver.QueueName;
        Assert.True(await QueueExistsAsync(queue));

        // Act: the connection goes away as a crashed process's would.
        var connection = await transport.GetConnectionAsync(consumer: true, TestCancellationToken);
        await connection.CloseAsync(TestCancellationToken);

        // Assert
        await WaitUntilAsync(async () => !await QueueExistsAsync(queue));
        await Assert.ThrowsAsync<MessageReceiverFaultedException>(() => receiver.ReceiveAsync(1, TimeSpan.FromSeconds(5), TestCancellationToken));
    }

    [Fact]
    public async Task Receiver_WhenItsConnectionCloses_FaultsAndItsDeliveriesAreDeliveredAgain()
    {
        // Arrange
        var source = MessageSource.Queue(NewName("conn-lost"));
        var receiver = await CreateReceiverAsync(source);
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("held", "id-held")], TestCancellationToken);
        var held = await ReceiveOneAsync(receiver);

        // Act
        var connection = await RabbitTransport.GetConnectionAsync(consumer: true, TestCancellationToken);
        await connection.CloseAsync(TestCancellationToken);

        // Assert
        await Assert.ThrowsAsync<MessageReceiverFaultedException>(() => receiver.ReceiveAsync(1, TimeSpan.FromSeconds(5), TestCancellationToken));
        await Assert.ThrowsAsync<MessageLeaseLostException>(() => receiver.RenewAsync(held, TestCancellationToken));
        await Assert.ThrowsAsync<MessageLeaseLostException>(() => receiver.CompleteAsync(held, TestCancellationToken));
        await receiver.DisposeAsync();

        await using var replacement = await CreateReceiverAsync(source);
        var again = await ReceiveOneAsync(replacement);
        Assert.Equal("id-held", again.MessageId);
        Assert.Equal(2, again.DeliveryCount);
        await replacement.CompleteAsync(again, TestCancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateReceiver_ForAQueueAnotherProcessDeleted_DeclaresItAgain(bool receiverOpen)
    {
        // Arrange
        var source = MessageSource.Queue(NewName("deleted"));
        var receiver = await CreateReceiverAsync(source);
        if (!receiverOpen)
            await receiver.DisposeAsync();

        var connection = await ConnectAsync();
        await using (var channel = await connection.CreateChannelAsync(cancellationToken: TestCancellationToken))
            await channel.QueueDeleteAsync(RabbitTransport.Names.Queue(source.Name), ifUnused: false, ifEmpty: false, cancellationToken: TestCancellationToken);

        if (receiverOpen)
        {
            await Assert.ThrowsAsync<MessageReceiverFaultedException>(() => receiver.ReceiveAsync(1, TimeSpan.FromSeconds(5), TestCancellationToken));
            await receiver.DisposeAsync();
        }

        // Act
        await using var replacement = await CreateReceiverAsync(source);
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("after", "id-after")], TestCancellationToken);

        // Assert
        await AssertOnlyReceivedAsync(replacement, "id-after");
    }

    [Fact]
    public async Task CreateReceiver_AfterTheBrokerLostItsDefinitions_DeclaresThemAgain()
    {
        Assert.SkipUnless(DelayedDelivery, "Without delayed delivery a queue binds to no exchange.");

        // Arrange
        string prefix = $"{Prefix}-reset-{Guid.NewGuid():N}";
        var transport = Track(new RabbitMQMessageTransport(CreateOptions() with { Prefix = prefix }));
        var source = MessageSource.Queue(NewName("reset"));
        try
        {
            await (await transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken)).DisposeAsync();
            await transport.SendAsync(MessageDestination.Queue(source.Name), [Message("before") with { DeliverAt = DateTimeOffset.UtcNow.AddSeconds(30) }], TestCancellationToken);
            await DeleteDefinitionsAsync(transport, source);

            // Act
            await using var replacement = await transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken);
            await transport.SendAsync(MessageDestination.Queue(source.Name), [new OutgoingMessage
            {
                MessageId = "id-delayed",
                Body = "delayed"u8.ToArray(),
                Headers = new Dictionary<string, string>(),
                DeliverAt = DateTimeOffset.UtcNow.AddSeconds(1)
            }], TestCancellationToken);

            // Assert
            await AssertOnlyReceivedAsync(replacement, "id-delayed");
            Assert.True(await QueueExistsAsync(transport.Names.Landing(transport.Names.For(source))));
        }
        finally
        {
            await DeleteDefinitionsAsync(transport, source);
        }
    }

    [Fact]
    public async Task Receive_WhileHoldingMoreUnsettledMessagesThanItsPrefetch_KeepsDelivering()
    {
        // Arrange
        var source = MessageSource.Queue(NewName("prefetch"));
        DeleteAfterTest(source);
        await using var receiver = await Transport.CreateReceiverAsync(source, new ReceiverOptions { PrefetchCount = 2 }, TestCancellationToken);
        await Transport.SendAsync(MessageDestination.Queue(source.Name), Enumerable.Range(0, 10).Select(i => Message($"m{i}", $"id-{i}")).ToList(), TestCancellationToken);

        // Act
        var held = await ReceiveAsync(receiver, 10);
        foreach (var message in held)
            await receiver.CompleteAsync(message, TestCancellationToken);

        // Assert
        Assert.Equal(10, held.Select(m => m.MessageId).Distinct().Count());
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("after", "id-after")], TestCancellationToken);
        await AssertOnlyReceivedAsync(receiver, "id-after");
    }

    [Fact]
    public async Task Receive_WhileMessagesAreSettledQuickly_RaisesThePrefetch()
    {
        // Arrange
        const int count = 600;
        var source = MessageSource.Queue(NewName("prefetch-raise"));
        DeleteAfterTest(source);
        await using var receiver = await Transport.CreateReceiverAsync(source, new ReceiverOptions { PrefetchCount = 1 }, TestCancellationToken);
        await Transport.SendAsync(MessageDestination.Queue(source.Name), Enumerable.Range(0, count).Select(i => Message($"m{i}", $"id-{i}")).ToList(), TestCancellationToken);

        // Act: settle each message as soon as it is received, as a fast handler does
        var ids = new List<string>();
        while (ids.Count < 300)
        {
            var batch = await receiver.ReceiveAsync(1, DeliveryTimeout, TestCancellationToken);
            Assert.NotEmpty(batch);
            await receiver.CompleteAsync(batch[0], TestCancellationToken);
            ids.Add(batch[0].MessageId);
        }
        int handled = ids.Count;

        // Assert: the broker delivered well beyond twice the one handler slot ahead of the handler
        var connection = await ConnectAsync();
        await using var channel = await connection.CreateChannelAsync(cancellationToken: TestCancellationToken);
        uint ready = (await channel.QueueDeclarePassiveAsync(RabbitTransport.Names.Queue(source.Name), TestCancellationToken)).MessageCount;
        Assert.True(ready < count - handled - 2, $"{ready} of the {count - handled} messages left were still ready.");

        var rest = await ReceiveAsync(receiver, count - handled);
        Assert.Equal(count, ids.Concat(rest.Select(m => m.MessageId)).Distinct().Count());
    }

    [Fact]
    public async Task Receive_WhileMessagesAreHeldForLong_KeepsThePrefetchAtTwiceTheHandlers()
    {
        // Arrange
        const int count = 20;
        var source = MessageSource.Queue(NewName("prefetch-slow"));
        DeleteAfterTest(source);
        await using var receiver = await Transport.CreateReceiverAsync(source, new ReceiverOptions { PrefetchCount = 1 }, TestCancellationToken);
        await Transport.SendAsync(MessageDestination.Queue(source.Name), Enumerable.Range(0, count).Select(i => Message($"m{i}", $"id-{i}")).ToList(), TestCancellationToken);

        // Act: a handler that takes longer than a buffered message should wait
        for (int i = 0; i < 3; i++)
        {
            var batch = await receiver.ReceiveAsync(1, DeliveryTimeout, TestCancellationToken);
            Assert.NotEmpty(batch);
            await Task.Delay(TimeSpan.FromMilliseconds(700), TestCancellationToken);
            await receiver.CompleteAsync(batch[0], TestCancellationToken);
        }

        // Assert: no more than the one being handled and one buffered left the queue
        await Task.Delay(TimeSpan.FromMilliseconds(500), TestCancellationToken);
        var connection = await ConnectAsync();
        await using var channel = await connection.CreateChannelAsync(cancellationToken: TestCancellationToken);
        uint ready = (await channel.QueueDeclarePassiveAsync(RabbitTransport.Names.Queue(source.Name), TestCancellationToken)).MessageCount;
        Assert.Equal((uint)(count - 3 - 2), ready);
    }

    [Fact]
    public async Task Abandon_OfAnInstanceSubscriptionMessageWithADelay_RedeliversItToTheSameSubscription()
    {
        Assert.SkipUnless(DelayedDelivery, "Delayed delivery is off.");

        // Arrange
        string topic = NewName("instance-retry");
        await using var receiver = await CreateReceiverAsync(MessageSource.InstanceSubscription(topic, "retry"));
        await Transport.SendAsync(MessageDestination.Topic(topic), [Message("retried", "id-retried")], TestCancellationToken);
        var first = await ReceiveOneAsync(receiver);
        var stopwatch = Stopwatch.StartNew();

        // Act
        await receiver.AbandonAsync(first, TimeSpan.FromSeconds(1), TestCancellationToken);
        var second = await ReceiveOneAsync(receiver, TimeSpan.FromSeconds(1) + DeliveryTimeout);

        // Assert
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromSeconds(0.9), $"Redelivered after {stopwatch.Elapsed}.");
        Assert.Equal("id-retried", second.MessageId);
        Assert.Equal(2, second.DeliveryCount);
        await receiver.CompleteAsync(second, TestCancellationToken);
    }

    [Fact]
    public async Task DelayedMessages_ForADeletedQueueOrATopicWithoutSubscriptions_AreDroppedWithoutHoldingUpOthers()
    {
        Assert.SkipUnless(DelayedDelivery, "Delayed delivery is off.");

        // Arrange: more expired messages without a route than a delay queue's dead-letter worker takes at once (32).
        var gone = MessageSource.Queue(NewName("delay-gone"));
        await (await CreateReceiverAsync(gone)).DisposeAsync();
        var dueSoon = DateTimeOffset.UtcNow.AddSeconds(1);
        await Transport.SendAsync(MessageDestination.Queue(gone.Name), Enumerable.Range(0, 40).Select(i => Message($"g{i}") with { DeliverAt = dueSoon }).ToList(), TestCancellationToken);
        await Transport.SendAsync(MessageDestination.Topic(NewName("delay-nobody")), Enumerable.Range(0, 40).Select(i => Message($"t{i}") with { DeliverAt = dueSoon }).ToList(), TestCancellationToken);
        await Transport.DeleteAsync(gone, TestCancellationToken);
        var source = MessageSource.Queue(NewName("delay-after"));
        await using var receiver = await CreateReceiverAsync(source);

        // Act
        await Task.Delay(TimeSpan.FromSeconds(1.5), TestCancellationToken);
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("after", "id-after") with { DeliverAt = DateTimeOffset.UtcNow.AddSeconds(1) }], TestCancellationToken);

        // Assert
        await AssertOnlyReceivedAsync(receiver, "id-after");
        Assert.False(await QueueExistsAsync(RabbitTransport.Names.Queue(gone.Name)));
    }

    [Fact]
    public async Task SendAsync_WithDelaysAtTheEdgesOfTheLevels_HoldsEachInItsHighestLevelAndDeliversItToItsTarget()
    {
        Assert.SkipUnless(DelayedDelivery, "Delayed delivery is off.");

        // Arrange: a prefix of its own, so its delay levels hold only this test's messages.
        var transport = Track(new RabbitMQMessageTransport(CreateOptions() with { Prefix = $"{Prefix}-levels-{Guid.NewGuid():N}" }));
        var queue = MessageSource.Queue(NewName("levels"));
        var subscription = MessageSource.Subscription(NewName("levels"), "sub");
        long max = (long)RabbitMQDelayLevels.MaxDelay.TotalSeconds;
        long[] delays = [1, 2, 3, (1L << 20) - 1, 1L << 20, (1L << 27) - 1, 1L << 27, max];
        try
        {
            await using var queueReceiver = await transport.CreateReceiverAsync(queue, new ReceiverOptions(), TestCancellationToken);
            await using var subscriptionReceiver = await transport.CreateReceiverAsync(subscription, new ReceiverOptions(), TestCancellationToken);

            // Act
            foreach (long seconds in delays)
            {
                var deliverAt = DateTimeOffset.UtcNow.AddSeconds(seconds);
                await transport.SendAsync(MessageDestination.Queue(queue.Name), [Message("queue", $"queue-{seconds}") with { DeliverAt = deliverAt }], TestCancellationToken);
                await transport.SendAsync(MessageDestination.Topic(subscription.Topic!), [Message("topic", $"topic-{seconds}") with { DeliverAt = deliverAt }], TestCancellationToken);
            }

            // Assert
            var connection = await ConnectAsync();
            await using (var channel = await connection.CreateChannelAsync(cancellationToken: TestCancellationToken))
            {
                foreach (var (level, count) in new[] { (19, 2u), (20, 2u), (26, 2u), (27, 4u) })
                    Assert.Equal(count, (await channel.QueueDeclarePassiveAsync(transport.Names.DelayLevel(level), TestCancellationToken)).MessageCount);
            }

            foreach (var (receiver, kind) in new[] { (queueReceiver, "queue"), (subscriptionReceiver, "topic") })
            {
                var received = await ReceiveAsync(receiver, 3, TimeSpan.FromSeconds(3) + DeliveryTimeout);
                Assert.Equal(new[] { $"{kind}-1", $"{kind}-2", $"{kind}-3" }, received.Select(m => m.MessageId).Order());
                foreach (var message in received)
                    await receiver.CompleteAsync(message, TestCancellationToken);
                await AssertNothingReceivedAsync(receiver, TimeSpan.FromSeconds(1));
            }
        }
        finally
        {
            await transport.DeleteAsync(queue, TestCancellationToken);
            await transport.DeleteAsync(subscription, TestCancellationToken);
            await DeleteDefinitionsAsync(transport, queue, transport.Names.Topic(subscription.Topic!));
        }
    }

    [Fact]
    public async Task CreateReceiverAsync_ForAQueueWhosePolicyRefusesMessagesWhenFull_WarnsThatItCanHoldUpDelays()
    {
        Assert.SkipUnless(DelayedDelivery, "Delayed delivery is off.");
        Assert.SkipWhen(Fixture.ManagementEndpoint is null, "The broker has no management API.");

        Log.MaxLogEntriesToStore = 1000;

        // Arrange
        var source = MessageSource.Queue(NewName("refuses"));
        DeleteAfterTest(source);
        var transport = Track(new RabbitMQMessageTransport(CreateOptions() with { ManagementEndpoint = Fixture.ManagementEndpoint }));
        string queue = transport.Names.Queue(source.Name);
        string policy = $"refuse-{Guid.NewGuid():N}";
        await PutPolicyAsync(policy, queue, new Dictionary<string, object> { ["max-length"] = 1, ["overflow"] = "reject-publish" });
        try
        {
            // Act
            await using var receiver = await transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken);

            // Assert
            await WaitUntilAsync(() => Task.FromResult(HasWarning($"RabbitMQ queue {queue} refuses messages once it holds 1 message")), ManagementTimeout);
        }
        finally
        {
            await DeletePolicyAsync(policy);
        }
    }

    [Fact]
    public async Task SendAsync_WhenAPolicyMakesTheDiscardQueueRefuseMessages_WarnsAndFailsPublishesWithoutSubscribers()
    {
        Assert.SkipUnless(DelayedDelivery, "Delayed delivery is off.");
        Assert.SkipWhen(Fixture.ManagementEndpoint is null, "The broker has no management API.");
        Log.MaxLogEntriesToStore = 1000;

        // Arrange: a quorum queue takes a policy's overflow over its own argument, so this one keeps one message and refuses the rest.
        var transport = Track(new RabbitMQMessageTransport(CreateOptions() with { Prefix = $"{Prefix}-discard-{Guid.NewGuid():N}", ManagementEndpoint = Fixture.ManagementEndpoint }));
        var source = MessageSource.Queue(NewName("discard"));
        string topic = NewName("nobody");
        string policy = $"refuse-{Guid.NewGuid():N}";
        await PutPolicyAsync(policy, transport.Names.Discard, new Dictionary<string, object> { ["overflow"] = "reject-publish" });
        try
        {
            // Act
            await (await transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken)).DisposeAsync();
            await transport.SendAsync(MessageDestination.Topic(topic), [Message("kept")], TestCancellationToken);
            var publish = transport.SendAsync(MessageDestination.Topic(topic), [Message("refused")], TestCancellationToken);

            // Assert
            await Assert.ThrowsAsync<MessageBusException>(() => publish);
            await WaitUntilAsync(() => Task.FromResult(HasWarning($"RabbitMQ queue {transport.Names.Discard} keeps one message and refuses every one after it")), ManagementTimeout);
        }
        finally
        {
            await DeletePolicyAsync(policy);
            await transport.DeleteAsync(source, TestCancellationToken);
            await DeleteDefinitionsAsync(transport, source, transport.Names.Topic(topic));
        }
    }

    [Fact]
    public async Task GetStatsAsync_WhileATargetRefusesItsDelayedMessages_WarnsThatItsLandingQueueIsHeldUp()
    {
        Assert.SkipUnless(DelayedDelivery, "Delayed delivery is off.");
        Assert.SkipWhen(Fixture.ManagementEndpoint is null, "The broker has no management API.");

        Log.MaxLogEntriesToStore = 1000;

        // Arrange: delay levels of its own, since the broker holds the refused messages for minutes.
        var transport = Track(new RabbitMQMessageTransport(CreateOptions() with
        {
            Prefix = $"{Prefix}-held-{Guid.NewGuid():N}",
            ManagementEndpoint = Fixture.ManagementEndpoint,
            DelayCheckInterval = TimeSpan.Zero,
            DelayStallWarningAfter = TimeSpan.Zero
        }));
        var full = MessageSource.Queue(NewName("full"));
        string queue = transport.Names.Queue(full.Name);
        string policy = $"refuse-{Guid.NewGuid():N}";
        await PutPolicyAsync(policy, queue, RefusingPolicy);
        try
        {
            await FillUntilItRefusesAsync(transport, MessageDestination.Queue(full.Name), queue);
            await SendDelaysItRefusesAsync(transport, MessageDestination.Queue(full.Name));

            // Act
            await WaitUntilAsync(async () =>
            {
                await transport.GetStatsAsync(full, TestCancellationToken);
                return HasWarning($"Landing queue {transport.Names.Landing(queue)} has had");
            }, ManagementTimeout);

            // Assert
            Assert.True(HasWarning($"delayed messages for {queue}"));
            Assert.False(HasWarning($"Delay level {transport.Names.DelayLevel(0)} has had"));
        }
        finally
        {
            await DeletePolicyAsync(policy);
            await transport.DeleteAsync(full, TestCancellationToken);
            await DeleteDefinitionsAsync(transport, full);
        }
    }

    [Fact]
    public async Task SendAsync_WithDelaysToAQueueThatRefusesThem_DeliversOtherQueuesDelays()
    {
        Assert.SkipUnless(DelayedDelivery, "Delayed delivery is off.");
        Assert.SkipWhen(Fixture.ManagementEndpoint is null, "The broker has no management API.");

        // Arrange: delay levels of its own, since the broker holds the refused messages for minutes.
        var transport = Track(new RabbitMQMessageTransport(CreateOptions() with { Prefix = $"{Prefix}-refuse-{Guid.NewGuid():N}" }));
        var full = MessageSource.Queue(NewName("full"));
        var other = MessageSource.Queue(NewName("other"));
        string queue = transport.Names.Queue(full.Name);
        string policy = $"refuse-{Guid.NewGuid():N}";
        await PutPolicyAsync(policy, queue, RefusingPolicy);
        try
        {
            await using var receiver = await transport.CreateReceiverAsync(other, new ReceiverOptions(), TestCancellationToken);
            await FillUntilItRefusesAsync(transport, MessageDestination.Queue(full.Name), queue);
            await SendDelaysItRefusesAsync(transport, MessageDestination.Queue(full.Name));

            // Act
            await transport.SendAsync(MessageDestination.Queue(other.Name), DelayedMessages("other", 5), TestCancellationToken);

            // Assert
            var received = await ReceiveAsync(receiver, 5);
            Assert.Equal(Enumerable.Range(0, 5).Select(i => $"id-other-{i}"), received.Select(m => m.MessageId).Order());
            Assert.Equal(2u, await CountMessagesAsync(queue));
        }
        finally
        {
            await DeletePolicyAsync(policy);
            await transport.DeleteAsync(full, TestCancellationToken);
            await transport.DeleteAsync(other, TestCancellationToken);
            await DeleteDefinitionsAsync(transport, full);
        }
    }

    [Fact]
    public async Task SendAsync_WithDelayedPublishesToATopicWhoseSubscriptionRefusesThem_DeliversOtherTopicsDelays()
    {
        Assert.SkipUnless(DelayedDelivery, "Delayed delivery is off.");
        Assert.SkipWhen(Fixture.ManagementEndpoint is null, "The broker has no management API.");

        // Arrange: delay levels of its own, since the broker holds the refused messages for minutes.
        var transport = Track(new RabbitMQMessageTransport(CreateOptions() with { Prefix = $"{Prefix}-refuse-topic-{Guid.NewGuid():N}" }));
        var full = MessageSource.Subscription(NewName("full"), "sub");
        var other = MessageSource.Subscription(NewName("other"), "sub");
        string queue = transport.Names.For(full);
        string policy = $"refuse-{Guid.NewGuid():N}";
        await PutPolicyAsync(policy, queue, RefusingPolicy);
        try
        {
            await (await transport.CreateReceiverAsync(full, new ReceiverOptions(), TestCancellationToken)).DisposeAsync();
            await using var receiver = await transport.CreateReceiverAsync(other, new ReceiverOptions(), TestCancellationToken);
            await FillUntilItRefusesAsync(transport, MessageDestination.Topic(full.Topic!), queue);
            await SendDelaysItRefusesAsync(transport, MessageDestination.Topic(full.Topic!));

            // Act
            await transport.SendAsync(MessageDestination.Topic(other.Topic!), DelayedMessages("other", 5), TestCancellationToken);

            // Assert
            var received = await ReceiveAsync(receiver, 5);
            Assert.Equal(Enumerable.Range(0, 5).Select(i => $"id-other-{i}"), received.Select(m => m.MessageId).Order());
            Assert.Equal(2u, await CountMessagesAsync(queue));
        }
        finally
        {
            await DeletePolicyAsync(policy);
            await transport.DeleteAsync(full, TestCancellationToken);
            await transport.DeleteAsync(other, TestCancellationToken);
            await DeleteDefinitionsAsync(transport, full, transport.Names.Topic(full.Topic!), transport.Names.Topic(other.Topic!));
        }
    }

    [Theory]
    [InlineData(MessageDestinationKind.Queue)]
    [InlineData(MessageDestinationKind.Topic)]
    [InlineData(MessageDestinationKind.Subscription)]
    public async Task SendAsync_WithADelay_DeclaresALandingQueueThatDeliversToTheTargetByName(MessageDestinationKind kind)
    {
        Assert.SkipUnless(DelayedDelivery, "Delayed delivery is off.");
        Assert.SkipWhen(Fixture.ManagementEndpoint is null, "The broker has no management API.");

        // Arrange
        var names = RabbitTransport.Names;
        string name = NewName("landing");
        var source = kind == MessageDestinationKind.Queue ? MessageSource.Queue(name) : MessageSource.Subscription(name, "sub");
        var destination = kind switch
        {
            MessageDestinationKind.Queue => MessageDestination.Queue(name),
            MessageDestinationKind.Topic => MessageDestination.Topic(name),
            _ => MessageDestination.Subscription(name, "sub")
        };
        string target = kind == MessageDestinationKind.Topic ? names.Topic(name) : names.For(source);
        string landing = names.Landing(target);
        try
        {
            await using var receiver = await CreateReceiverAsync(source);
            await Transport.SendAsync(destination, [Message("now", "id-now")], TestCancellationToken);
            await AssertOnlyReceivedAsync(receiver, "id-now");
            bool declaredBeforeADelay = await QueueExistsAsync(landing);

            // Act
            await Transport.SendAsync(destination, [Message("delayed", "id-delayed") with { DeliverAt = DateTimeOffset.UtcNow.AddSeconds(1) }], TestCancellationToken);

            // Assert
            await AssertOnlyReceivedAsync(receiver, "id-delayed");
            Assert.False(declaredBeforeADelay);
            using var details = await GetManagementJsonAsync($"api/queues/%2F/{Uri.EscapeDataString(landing)}");
            var arguments = details.RootElement.GetProperty("arguments");
            Assert.Equal("quorum", arguments.GetProperty("x-queue-type").GetString());
            Assert.Equal(0, arguments.GetProperty("x-message-ttl").GetInt32());
            Assert.Equal(names.DelayFinal, arguments.GetProperty("x-dead-letter-exchange").GetString());
            Assert.Equal("at-least-once", arguments.GetProperty("x-dead-letter-strategy").GetString());
            Assert.Equal("reject-publish", arguments.GetProperty("x-overflow").GetString());
            using var bindings = await GetManagementJsonAsync($"api/queues/%2F/{Uri.EscapeDataString(landing)}/bindings");
            Assert.Contains(bindings.RootElement.EnumerateArray(), b => b.GetProperty("source").GetString() == names.DelayDelivery
                && b.GetProperty("routing_key").GetString() == RabbitMQDelayLevels.DeliveryBindingKey(target));
            using var targetBindings = await GetManagementJsonAsync(kind == MessageDestinationKind.Topic
                ? $"api/exchanges/%2F/{Uri.EscapeDataString(target)}/bindings/destination"
                : $"api/queues/%2F/{Uri.EscapeDataString(target)}/bindings");
            Assert.Contains(targetBindings.RootElement.EnumerateArray(), b => b.GetProperty("source").GetString() == names.DelayFinal
                && b.GetProperty("routing_key").GetString() == RabbitMQDelayLevels.DeliveryBindingKey(target));
        }
        finally
        {
            await DeleteQueuesAsync(names.Landing(names.Topic(name)));
        }
    }

    [Fact]
    public async Task SendAsync_WithADelayAfterAnOperatorDeletedTheLandingQueue_DeliversItAndAnotherProcessDeclaresItAgain()
    {
        Assert.SkipUnless(DelayedDelivery, "Delayed delivery is off.");

        // Arrange
        var source = MessageSource.Queue(NewName("landing-gone"));
        string landing = RabbitTransport.Names.Landing(RabbitTransport.Names.For(source));
        await using var receiver = await CreateReceiverAsync(source);
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("first", "id-first") with { DeliverAt = DateTimeOffset.UtcNow.AddSeconds(1) }], TestCancellationToken);
        await AssertOnlyReceivedAsync(receiver, "id-first");
        Assert.True(await QueueExistsAsync(landing));
        await DeleteQueuesAsync(landing);

        // Act
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("second", "id-second") with { DeliverAt = DateTimeOffset.UtcNow.AddSeconds(1) }], TestCancellationToken);
        await AssertOnlyReceivedAsync(receiver, "id-second");
        var restarted = Track(new RabbitMQMessageTransport(CreateOptions()));
        await restarted.SendAsync(MessageDestination.Queue(source.Name), [Message("third", "id-third") with { DeliverAt = DateTimeOffset.UtcNow.AddSeconds(1) }], TestCancellationToken);

        // Assert
        await AssertOnlyReceivedAsync(receiver, "id-third");
        Assert.True(await QueueExistsAsync(landing));
    }

    [Fact]
    public async Task DelayedMessage_ForATargetWithoutALandingQueue_IsDeliveredThroughTheAlternateExchange()
    {
        Assert.SkipUnless(DelayedDelivery, "Delayed delivery is off.");

        // Arrange: a queue that has never had a delayed message, so it has no landing queue, and a delayed message for
        // it from another producer, as one from before its landing queue existed would be.
        var source = MessageSource.Queue(NewName("no-landing"));
        string target = RabbitTransport.Names.For(source);
        await using var receiver = await CreateReceiverAsync(source);
        var connection = await ConnectAsync();
        await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), TestCancellationToken);

        // Act
        await channel.BasicPublishAsync(RabbitTransport.Names.DelayLevel(0), RabbitMQDelayLevels.RoutingKey(1, target), true, new BasicProperties { MessageId = "id-raw" },
            "raw"u8.ToArray(), TestCancellationToken);

        // Assert
        await AssertOnlyReceivedAsync(receiver, "id-raw");
        Assert.False(await QueueExistsAsync(RabbitTransport.Names.Landing(target)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbandonAsync_WithADelay_RedeliversThroughTheSourcesLandingQueue(bool subscription)
    {
        Assert.SkipUnless(DelayedDelivery, "Delayed delivery is off.");

        // Arrange
        var source = subscription ? MessageSource.Subscription(NewName("retry-landing"), "sub") : MessageSource.Queue(NewName("retry-landing"));
        await using var receiver = await CreateReceiverAsync(source);
        await Transport.SendAsync(MessageDestination.For(source), [Message("retried", "id-retried")], TestCancellationToken);
        var first = await ReceiveOneAsync(receiver);

        // Act
        await receiver.AbandonAsync(first, TimeSpan.FromSeconds(1), TestCancellationToken);
        var second = await ReceiveOneAsync(receiver, TimeSpan.FromSeconds(1) + DeliveryTimeout);

        // Assert
        Assert.Equal("id-retried", second.MessageId);
        Assert.Equal(2, second.DeliveryCount);
        Assert.True(await QueueExistsAsync(RabbitTransport.Names.Landing(RabbitTransport.Names.For(source))));
        await receiver.CompleteAsync(second, TestCancellationToken);
    }

    [Fact]
    public async Task AbandonAsync_WithADelayOnAnInstanceSubscription_RedeliversWithoutALandingQueueOfItsOwn()
    {
        Assert.SkipUnless(DelayedDelivery, "Delayed delivery is off.");

        // Arrange
        string topic = NewName("instance-landing");
        var receiver = (RabbitMQMessageReceiver)await CreateReceiverAsync(MessageSource.InstanceSubscription(topic, "retry"));
        string landing = RabbitTransport.Names.Landing(receiver.QueueName);
        try
        {
            await Transport.SendAsync(MessageDestination.Topic(topic), [Message("published", "id-published") with { DeliverAt = DateTimeOffset.UtcNow.AddSeconds(1) }], TestCancellationToken);
            var first = await ReceiveOneAsync(receiver);

            // Act
            await receiver.AbandonAsync(first, TimeSpan.FromSeconds(1), TestCancellationToken);
            var second = await ReceiveOneAsync(receiver, TimeSpan.FromSeconds(1) + DeliveryTimeout);
            await receiver.CompleteAsync(second, TestCancellationToken);
            bool landed = await QueueExistsAsync(landing);
            await receiver.DisposeAsync();

            // Assert
            Assert.Equal("id-published", second.MessageId);
            Assert.Equal(2, second.DeliveryCount);
            Assert.False(landed);
            Assert.False(await QueueExistsAsync(receiver.QueueName));
            Assert.False(await QueueExistsAsync(landing));
        }
        finally
        {
            await receiver.DisposeAsync();
            await DeleteQueuesAsync(RabbitTransport.Names.Landing(RabbitTransport.Names.Topic(topic)));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteAsync_OfASourceWithALandingQueue_DeletesTheLandingQueue(bool subscription)
    {
        Assert.SkipUnless(DelayedDelivery, "Delayed delivery is off.");

        // Arrange
        var source = subscription ? MessageSource.Subscription(NewName("delete-landing"), "sub") : MessageSource.Queue(NewName("delete-landing"));
        string landing = RabbitTransport.Names.Landing(RabbitTransport.Names.For(source));
        await (await CreateReceiverAsync(source)).DisposeAsync();
        await Transport.SendAsync(MessageDestination.For(source), [Message("delayed") with { DeliverAt = DateTimeOffset.UtcNow.AddSeconds(30) }], TestCancellationToken);
        Assert.True(await QueueExistsAsync(landing));

        // Act
        await Transport.DeleteAsync(source, TestCancellationToken);

        // Assert
        Assert.False(await QueueExistsAsync(landing));
        Assert.False(await QueueExistsAsync(RabbitTransport.Names.For(source)));
    }

    [Fact]
    public async Task DeleteAsync_WithDelayedDeliveryOff_DeletesTheLandingQueuesAnotherProcessDeclared()
    {
        Assert.SkipWhen(DelayedDelivery, "Runs with a transport that has delayed delivery off.");

        // Arrange: a process with delayed delivery declared the landing queues of the source and of its dead-letter queue.
        var withDelays = Track(new RabbitMQMessageTransport(CreateOptions() with { DelayedDelivery = true }));
        var source = MessageSource.Queue(NewName("delete-landing-off"));
        string landing = withDelays.Names.Landing(withDelays.Names.For(source));
        string deadLetterLanding = withDelays.Names.Landing(withDelays.Names.DeadLetter(source));
        try
        {
            await withDelays.SendAsync(MessageDestination.Queue(source.Name), [Message("delayed") with { DeliverAt = DateTimeOffset.UtcNow.AddSeconds(30) }], TestCancellationToken);
            await withDelays.Topology.EnsureLandingAsync(withDelays.Names.DeadLetter(source), TestCancellationToken);
            Assert.True(await QueueExistsAsync(landing));
            Assert.True(await QueueExistsAsync(deadLetterLanding));

            // Act
            await Transport.DeleteAsync(source, TestCancellationToken);

            // Assert
            Assert.False(await QueueExistsAsync(RabbitTransport.Names.For(source)));
            Assert.False(await QueueExistsAsync(landing));
            Assert.False(await QueueExistsAsync(deadLetterLanding));
        }
        finally
        {
            await DeleteQueuesAsync(landing, deadLetterLanding);
        }
    }

    [Fact]
    public async Task Names_ThatJoinTheSameOrPassTheLengthLimit_DoNotCollide()
    {
        // Arrange
        string name = NewName("collide");
        string tooLong = name + new string('x', 250);
        var sources = new[]
        {
            MessageSource.Queue(tooLong + "1"),
            MessageSource.Queue(tooLong + "2"),
            MessageSource.Queue($"{name}..dots"),
            MessageSource.Subscription($"{name}.a", "b"),
            MessageSource.Subscription(name, "a.b")
        };
        var receivers = new List<IMessageReceiver>();
        foreach (var source in sources)
            receivers.Add(await CreateReceiverAsync(source));

        // Act
        await Transport.SendAsync(MessageDestination.For(sources[0]), [Message("one", "id-one")], TestCancellationToken);
        await Transport.SendAsync(MessageDestination.For(sources[1]), [Message("two", "id-two")], TestCancellationToken);
        await Transport.SendAsync(MessageDestination.For(sources[2]), [Message("dots", "id-dots") with { DeliverAt = DelayedDelivery ? DateTimeOffset.UtcNow.AddSeconds(1) : null }], TestCancellationToken);
        await Transport.SendAsync(MessageDestination.Topic($"{name}.a"), [Message("ab", "id-a.b")], TestCancellationToken);
        await Transport.SendAsync(MessageDestination.Topic(name), [Message("a.b", "id-a/b")], TestCancellationToken);

        // Assert
        Assert.All(sources.Select(RabbitTransport.Names.For), queue => Assert.True(queue.Length <= RabbitMQMessageTransportNames.MaxNameLength, queue));
        string[] expected = ["id-one", "id-two", "id-dots", "id-a.b", "id-a/b"];
        for (int i = 0; i < receivers.Count; i++)
            await AssertOnlyReceivedAsync(receivers[i], expected[i]);

        foreach (var receiver in receivers)
            await receiver.DisposeAsync();
    }

    [Fact]
    public async Task Messages_FromOtherProducers_AreReceivedWithTheirIdAndHeaders()
    {
        // Arrange
        var source = MessageSource.Queue(NewName("foreign"));
        await using var receiver = await CreateReceiverAsync(source);
        var connection = await ConnectAsync();
        await using var channel = await connection.CreateChannelAsync(cancellationToken: TestCancellationToken);
        string queue = RabbitTransport.Names.Queue(source.Name);

        // Act
        await channel.BasicPublishAsync(String.Empty, queue, true, new BasicProperties
        {
            MessageId = "foreign-1",
            Headers = new Dictionary<string, object?> { ["tenant"] = Encoding.UTF8.GetBytes("acme"), ["count"] = 3, ["flag"] = true, ["x-custom"] = Encoding.UTF8.GetBytes("broker-ish") }
        }, Encoding.UTF8.GetBytes("hello"), TestCancellationToken);
        await channel.BasicPublishAsync(String.Empty, queue, true, new BasicProperties(), Encoding.UTF8.GetBytes("no id"), TestCancellationToken);

        // Assert
        var messages = await ReceiveAsync(receiver, 2);
        var first = Assert.Single(messages, m => m.MessageId == "foreign-1");
        Assert.Equal("hello", Encoding.UTF8.GetString(first.Body.Span));
        Assert.Equal(new Dictionary<string, string> { ["tenant"] = "acme", ["count"] = "3", ["flag"] = "true" }, first.Headers);
        Assert.Equal(1, first.DeliveryCount);
        var second = Assert.Single(messages, m => m.MessageId != "foreign-1");
        Assert.False(String.IsNullOrEmpty(second.MessageId));
        Assert.Equal("no id", Encoding.UTF8.GetString(second.Body.Span));
    }

    [Fact]
    public async Task Headers_WithNamesTheBrokerOrClientOwnOrThatAreLarge_RoundTripUnchanged()
    {
        // Arrange
        var source = MessageSource.Queue(NewName("odd-headers"));
        await using var receiver = await CreateReceiverAsync(source);
        var aliased = new Dictionary<string, string>
        {
            ["x-custom"] = "1",
            ["X-Death"] = "not the broker's",
            ["x-delivery-count"] = "99",
            ["x-foundatio-format"] = "app",
            ["CC"] = "not routing",
            ["bcc"] = "hidden",
            ["traceparent"] = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
            [""] = "empty name",
            ["plain"] = "kept"
        };
        var longName = new Dictionary<string, string> { [new string('n', 300)] = "long name", ["plain"] = "kept" };
        var largeValue = new Dictionary<string, string> { ["large"] = new string('v', 100 * 1024), ["x-other"] = "x" };

        // Act
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [
            Message("aliased", "id-aliased", aliased),
            Message("long name", "id-long-name", longName),
            Message("large value", "id-large-value", largeValue)
        ], TestCancellationToken);

        // Assert
        var messages = (await ReceiveAsync(receiver, 3)).ToDictionary(m => m.MessageId);
        Assert.Equal(aliased, messages["id-aliased"].Headers);
        Assert.Equal(longName, messages["id-long-name"].Headers);
        Assert.Equal(largeValue, messages["id-large-value"].Headers);
        Assert.Equal("large value", Encoding.UTF8.GetString(messages["id-large-value"].Body.Span));
    }

    [Fact]
    public async Task Headers_WithPlainNames_AreAmqpHeadersOtherClientsCanRead()
    {
        // Arrange
        var source = MessageSource.Queue(NewName("interop"));
        DeleteAfterTest(source);
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("{\"a\":1}", "id-interop", new Dictionary<string, string> { [KnownHeaders.MessageType] = "orders.created" })], TestCancellationToken);
        var connection = await ConnectAsync();
        await using var channel = await connection.CreateChannelAsync(cancellationToken: TestCancellationToken);

        // Act
        var result = await channel.BasicGetAsync(RabbitTransport.Names.Queue(source.Name), autoAck: true, TestCancellationToken);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("id-interop", result.BasicProperties.MessageId);
        Assert.Equal("{\"a\":1}", Encoding.UTF8.GetString(result.Body.Span));
        Assert.Equal("orders.created", Encoding.UTF8.GetString((byte[])result.BasicProperties.Headers![KnownHeaders.MessageType]!));
    }

    [Fact]
    public async Task Headers_WhileRabbitMQClientIsTraced_RoundTripUnchanged()
    {
        // Arrange: RabbitMQ.Client writes its own traceparent into the headers of every message it publishes while traced.
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name.StartsWith("RabbitMQ.Client", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);
        var source = MessageSource.Queue(NewName("traced"));
        await using var receiver = await CreateReceiverAsync(source);
        var traced = new Dictionary<string, string> { [KnownHeaders.TraceParent] = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01", [KnownHeaders.TraceState] = "a=b" };

        // Act
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("traced", "id-traced", traced), Message("untraced", "id-untraced")], TestCancellationToken);

        // Assert
        var messages = (await ReceiveAsync(receiver, 2)).ToDictionary(m => m.MessageId);
        Assert.Equal(traced, messages["id-traced"].Headers);
        Assert.Empty(messages["id-untraced"].Headers);
    }

    [Fact]
    public async Task CreateReceiver_ForAQueueThatExistsWithOtherArguments_FailsWithAClearError()
    {
        // Arrange
        var source = MessageSource.Queue(NewName("conflict"));
        string queue = RabbitTransport.Names.Queue(source.Name);
        var connection = await ConnectAsync();
        await using var channel = await connection.CreateChannelAsync(cancellationToken: TestCancellationToken);
        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false, arguments: new Dictionary<string, object?> { ["x-queue-type"] = "classic" },
            cancellationToken: TestCancellationToken);
        DeleteAfterTest(source);

        // Act
        var exception = await Assert.ThrowsAsync<MessageBusException>(() => Transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken));

        // Assert
        Assert.Contains("different arguments", exception.Message);
        Assert.Contains(queue, exception.Message);
        var other = MessageSource.Queue(NewName("after-conflict"));
        await using var receiver = await CreateReceiverAsync(other);
        await Transport.SendAsync(MessageDestination.Queue(other.Name), [Message("fine", "id-fine")], TestCancellationToken);
        await AssertOnlyReceivedAsync(receiver, "id-fine");
    }

    [Fact]
    public async Task CreateReceiver_WithTheDelayExchangeOfAnEarlierPreview_FailsPointingToTheUpgradeSteps()
    {
        Assert.SkipUnless(DelayedDelivery, "Delayed delivery is off.");

        // Arrange: a preview before landing queues declared <prefix>.delay with the discard exchange as its alternate exchange.
        var transport = Track(new RabbitMQMessageTransport(CreateOptions() with { Prefix = $"{Prefix}-preview-{Guid.NewGuid():N}" }));
        var names = transport.Names;
        var source = MessageSource.Queue(NewName("preview"));
        var connection = await ConnectAsync();
        await using (var channel = await connection.CreateChannelAsync(cancellationToken: TestCancellationToken))
        {
            await channel.ExchangeDeclareAsync(names.DelayDelivery, ExchangeType.Topic, durable: true, autoDelete: false,
                arguments: new Dictionary<string, object?> { ["alternate-exchange"] = names.Discard }, cancellationToken: TestCancellationToken);
        }

        try
        {
            // Act
            var exception = await Assert.ThrowsAsync<MessageBusException>(() => transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken));

            // Assert
            Assert.Contains($"declare exchange {names.DelayDelivery}", exception.Message);
            Assert.Contains($"copy each of its bindings to {names.DelayFinal}", exception.Message);
            Assert.Contains("\"Upgrading from an earlier preview\"", exception.Message);
        }
        finally
        {
            await DeleteDefinitionsAsync(transport, source);
        }
    }

    [Fact]
    public async Task Send_ToAQueueAnotherProcessDeleted_DeclaresItAgainAndKeepsTheMessage()
    {
        // Arrange
        var source = MessageSource.Queue(NewName("redeclare"));
        DeleteAfterTest(source);
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("before", "id-before")], TestCancellationToken);
        await PeerTransport.DeleteAsync(source, TestCancellationToken);

        // Act
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("after", "id-after")], TestCancellationToken);

        // Assert
        await using var receiver = await CreateReceiverAsync(source);
        await AssertOnlyReceivedAsync(receiver, "id-after");
    }

    [Fact]
    public async Task SendAsync_WhenAHostAcceptsConnectionsButNeverAnswers_ConnectsToAnotherHost()
    {
        // Arrange: a node that is paused, or a TCP load balancer whose backend is down, accepts the connection and
        // never starts the AMQP handshake. The client shuffles the hosts, so each attempt may pick it first.
        var broker = new Uri(Fixture.ConnectionString);
        await using var silent = TcpTestProxy.Silent();
        var source = MessageSource.Queue(NewName("stalled-host"));
        DeleteAfterTest(source);
        var options = CreateOptions() with
        {
            Hosts = [silent.Host, $"{broker.Host}:{broker.Port}"],
            ConfigureConnectionFactory = f => f.HandshakeContinuationTimeout = TimeSpan.FromSeconds(1)
        };

        // Act
        for (int i = 0; i < 6; i++)
        {
            await using var transport = new RabbitMQMessageTransport(options);
            await transport.SendAsync(MessageDestination.Queue(source.Name), [Message($"m{i}", $"id-{i}")], TestCancellationToken);
        }

        // Assert
        await using var receiver = await CreateReceiverAsync(source);
        var received = await ReceiveAsync(receiver, 6);
        Assert.Equal(Enumerable.Range(0, 6).Select(i => $"id-{i}"), received.Select(m => m.MessageId).Order());
    }

    [Fact]
    public async Task GetStats_WithTheManagementApi_CountsInFlightMessages()
    {
        Assert.SkipWhen(Fixture.ManagementEndpoint is null, "The test broker has no management endpoint.");

        // Arrange
        var transport = Track(new RabbitMQMessageTransport(CreateOptions() with { ManagementEndpoint = Fixture.ManagementEndpoint }));
        var source = MessageSource.Queue(NewName("stats-mgmt"));
        DeleteAfterTest(source);
        // A prefetch of one buffers two deliveries: one handled and one ahead. Both count as in flight.
        await using var receiver = await transport.CreateReceiverAsync(source, new ReceiverOptions { PrefetchCount = 1 }, TestCancellationToken);
        await transport.SendAsync(MessageDestination.Queue(source.Name), [Message("a"), Message("b"), Message("c")], TestCancellationToken);
        var held = await ReceiveOneAsync(receiver);

        // Act
        var leased = await WaitForStatsAsync(transport, source, s => s is { Queued: 1, InFlight: 2 });
        await receiver.DeadLetterAsync(held, "counted", TestCancellationToken);
        var deadLettered = await WaitForStatsAsync(transport, source, s => s.DeadLettered == 1);

        // Assert
        Assert.Null(leased.Delayed);
        Assert.Equal(1, deadLettered.DeadLettered);
        var missing = await transport.GetStatsAsync(MessageSource.Queue(NewName("stats-mgmt-none")), TestCancellationToken);
        Assert.Equal(new MessageSourceStats { Source = missing!.Source, Queued = 0, InFlight = 0, DeadLettered = 0 }, missing);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        foreach (var connection in _connections)
            await connection.DisposeAsync();
    }

    private async Task<IConnection> ConnectAsync()
    {
        var connection = await new ConnectionFactory { Uri = new Uri(Fixture.ConnectionString) }.CreateConnectionAsync(TestCancellationToken);
        _connections.Add(connection);
        return connection;
    }

    /// <summary>
    /// Deletes a source's queue and landing queue, the given topics' exchanges and landing queues, and every exchange and
    /// queue the transport shares, as a broker reset does.
    /// </summary>
    private async Task DeleteDefinitionsAsync(RabbitMQMessageTransport transport, MessageSource source, params string[] topicExchanges)
    {
        var names = transport.Names;
        var connection = await ConnectAsync();
        await using var channel = await connection.CreateChannelAsync(cancellationToken: TestCancellationToken);
        foreach (string queue in new[] { names.Queue(source.Name), names.Landing(names.Queue(source.Name)) }.Concat(topicExchanges.Select(names.Landing)))
            await channel.QueueDeleteAsync(queue, ifUnused: false, ifEmpty: false, cancellationToken: TestCancellationToken);
        foreach (string exchange in topicExchanges)
            await channel.ExchangeDeleteAsync(exchange, ifUnused: false, cancellationToken: TestCancellationToken);

        for (int level = 0; level < RabbitMQDelayLevels.Count; level++)
        {
            await channel.QueueDeleteAsync(names.DelayLevel(level), ifUnused: false, ifEmpty: false, cancellationToken: TestCancellationToken);
            await channel.ExchangeDeleteAsync(names.DelayLevel(level), ifUnused: false, cancellationToken: TestCancellationToken);
        }

        await channel.ExchangeDeleteAsync(names.DelayDelivery, ifUnused: false, cancellationToken: TestCancellationToken);
        await channel.ExchangeDeleteAsync(names.DelayFinal, ifUnused: false, cancellationToken: TestCancellationToken);
        await channel.QueueDeleteAsync(names.Discard, ifUnused: false, ifEmpty: false, cancellationToken: TestCancellationToken);
        await channel.ExchangeDeleteAsync(names.Discard, ifUnused: false, cancellationToken: TestCancellationToken);
    }

    private async Task DeleteQueuesAsync(params string[] queues)
    {
        var connection = await ConnectAsync();
        await using var channel = await connection.CreateChannelAsync(cancellationToken: TestCancellationToken);
        foreach (string queue in queues)
            await channel.QueueDeleteAsync(queue, ifUnused: false, ifEmpty: false, cancellationToken: TestCancellationToken);
    }

    /// <summary>A policy that keeps one message in a queue and refuses the rest.</summary>
    private static Dictionary<string, object> RefusingPolicy => new() { ["max-length"] = 1, ["overflow"] = "reject-publish" };

    private static List<OutgoingMessage> DelayedMessages(string name, int count)
    {
        var deliverAt = DateTimeOffset.UtcNow.AddSeconds(1);
        return Enumerable.Range(0, count).Select(i => Message($"{name} {i}", $"id-{name}-{i}") with { DeliverAt = deliverAt }).ToList();
    }

    /// <summary>
    /// Fills <paramref name="queue"/>, which <see cref="RefusingPolicy"/> applies to. Quorum queue limits are soft: the
    /// full queue still takes a second message, and the dead-letter worker that delivered it then knows it refuses, so
    /// it rejects every later one, however close together they expire.
    /// </summary>
    private async Task FillUntilItRefusesAsync(RabbitMQMessageTransport transport, MessageDestination destination, string queue)
    {
        await transport.SendAsync(destination, [Message("fills it")], TestCancellationToken);
        await transport.SendAsync(destination, [Message("overflows it") with { DeliverAt = DateTimeOffset.UtcNow.AddSeconds(1) }], TestCancellationToken);
        await WaitUntilAsync(async () => await CountMessagesAsync(queue) == 2, ManagementTimeout);
    }

    /// <summary>
    /// Sends more one-second delays to <paramref name="destination"/> than a dead-letter worker forwards at once (32),
    /// and waits until they have all come due, so the broker has tried to deliver them.
    /// </summary>
    private async Task SendDelaysItRefusesAsync(RabbitMQMessageTransport transport, MessageDestination destination)
    {
        await transport.SendAsync(destination, DelayedMessages("refused", 40), TestCancellationToken);
        await WaitUntilAsync(async () => await CountMessagesAsync(transport.Names.DelayLevel(0)) == 0);
    }

    private async Task<JsonDocument> GetManagementJsonAsync(string path)
    {
        using var http = CreateManagementClient();
        using var response = await http.GetAsync(path, TestCancellationToken);
        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(TestCancellationToken), cancellationToken: TestCancellationToken);
    }

    /// <summary>The management API lists a new queue's policy, and refreshes its counts, from its statistics, every 5 seconds by default.</summary>
    private static readonly TimeSpan ManagementTimeout = TimeSpan.FromSeconds(45);

    private bool HasWarning(string text) => Log.LogEntries.Any(e => e.LogLevel == LogLevel.Warning && e.Message.Contains(text, StringComparison.Ordinal));

    private async Task PutPolicyAsync(string name, string queue, Dictionary<string, object> definition)
    {
        using var http = CreateManagementClient();
        var body = new Dictionary<string, object> { ["pattern"] = $"^{Regex.Escape(queue)}$", ["definition"] = definition, ["apply-to"] = "queues", ["priority"] = 100 };
        using var response = await http.PutAsync($"api/policies/%2F/{name}", new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"), TestCancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task DeletePolicyAsync(string name)
    {
        using var http = CreateManagementClient();
        using var response = await http.DeleteAsync($"api/policies/%2F/{name}", TestCancellationToken);
    }

    private HttpClient CreateManagementClient()
    {
        string userInfo = Uri.UnescapeDataString(new Uri(Fixture.ConnectionString).UserInfo);
        var http = new HttpClient { BaseAddress = new Uri(Fixture.ManagementEndpoint!.AbsoluteUri.TrimEnd('/') + "/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(userInfo)));
        return http;
    }

    private async Task<uint> CountMessagesAsync(string queue)
    {
        var connection = await ConnectAsync();
        await using var channel = await connection.CreateChannelAsync(cancellationToken: TestCancellationToken);
        return (await channel.QueueDeclarePassiveAsync(queue, TestCancellationToken)).MessageCount;
    }

    private async Task<bool> QueueExistsAsync(string queue)
    {
        var connection = await ConnectAsync();
        await using var channel = await connection.CreateChannelAsync(cancellationToken: TestCancellationToken);
        try
        {
            await channel.QueueDeclarePassiveAsync(queue, TestCancellationToken);
            return true;
        }
        catch (OperationInterruptedException ex) when (ex.ShutdownReason?.ReplyCode == 404)
        {
            return false;
        }
        catch (OperationInterruptedException ex) when (ex.ShutdownReason?.ReplyCode == 405)
        {
            // An exclusive queue of another connection.
            return true;
        }
    }

    private async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DeliveryTimeout);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition was not met in time.");
            await Task.Delay(100, TestCancellationToken);
        }
    }

    private async Task<MessageSourceStats> WaitForStatsAsync(IMessageTransport transport, MessageSource source, Func<MessageSourceStats, bool> condition)
    {
        // The management API's counts lag by up to its statistics interval, 5 seconds by default.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            var stats = await transport.GetStatsAsync(source, TestCancellationToken);
            Assert.NotNull(stats);
            if (condition(stats))
                return stats;

            Assert.True(DateTime.UtcNow < deadline, $"{source} counted {stats.Queued} queued, {stats.InFlight} in flight and {stats.DeadLettered} dead-lettered.");
            await Task.Delay(250, TestCancellationToken);
        }
    }
}
