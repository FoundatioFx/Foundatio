using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;
using Foundatio.Tests.Messaging;
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
    public async Task GetStats_WithTheManagementApi_CountsInFlightMessages()
    {
        Assert.SkipWhen(Fixture.ManagementEndpoint is null, "The test broker has no management endpoint.");

        // Arrange
        var transport = Track(new RabbitMQMessageTransport(CreateOptions() with { ManagementEndpoint = Fixture.ManagementEndpoint }));
        var source = MessageSource.Queue(NewName("stats-mgmt"));
        DeleteAfterTest(source);
        await using var receiver = await transport.CreateReceiverAsync(source, new ReceiverOptions { PrefetchCount = 1 }, TestCancellationToken);
        await transport.SendAsync(MessageDestination.Queue(source.Name), [Message("a"), Message("b"), Message("c")], TestCancellationToken);
        var held = await ReceiveOneAsync(receiver);

        // Act
        var leased = await WaitForStatsAsync(transport, source, s => s is { Queued: 2, InFlight: 1 });
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

    private async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + DeliveryTimeout;
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
