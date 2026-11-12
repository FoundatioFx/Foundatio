using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Confluent.Kafka;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;
using Foundatio.Tests.Messaging;
using Xunit;

namespace Foundatio.Kafka.Tests.Messaging;

public class KafkaMessageTransportTests(ITestOutputHelper output) : MessageTransportTestsBase(output)
{
    private const string Prefix = "foundatio-tests";

    protected override TimeSpan DeliveryTimeout => TimeSpan.FromSeconds(20);

    protected override IMessageTransport CreateTransport() => new KafkaMessageTransport(CreateOptions());

    [Fact]
    public async Task Rebalance_ReleasesUnsettledMessagesOfAMovedPartitionWithAHigherDeliveryCount()
    {
        var transport = Track(new KafkaMessageTransport(CreateOptions() with
        {
            Partitions = 2,
            RebalanceGracePeriod = TimeSpan.FromSeconds(1),
            ConfigureProducer = c => c.StickyPartitioningLingerMs = 0
        }));
        var source = MessageSource.Queue(NewName("rebalance"));
        await using var first = await transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken);
        await transport.SendAsync(MessageDestination.Queue(source.Name), Enumerable.Range(0, 20).Select(i => Message($"m{i}", $"id-{i}")).ToList(), TestCancellationToken);
        var held = await ReceiveAsync(first, 20);

        await using var second = await transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken);
        var lost = new HashSet<string>();
        await WaitForAsync(() =>
        {
            foreach (var message in held)
            {
                if (first.RenewAsync(message, TestCancellationToken).Exception?.InnerException is MessageLeaseLostException)
                    lost.Add(message.MessageId);
            }

            return lost.Count > 0;
        });

        // The rebalance is over once the moved partition's messages are released; settle the rest on the first receiver.
        await Task.Delay(TimeSpan.FromSeconds(2), TestCancellationToken);
        foreach (var message in held.Where(m => !lost.Contains(m.MessageId)))
            await first.CompleteAsync(message, TestCancellationToken);

        Assert.InRange(lost.Count, 1, 19);
        var copies = new List<(IMessageReceiver Receiver, IncomingMessage Message)>();
        await WaitForAsync(() =>
        {
            foreach (var receiver in new[] { first, second })
                copies.AddRange(receiver.ReceiveAsync(20, TimeSpan.FromMilliseconds(100), TestCancellationToken).GetAwaiter().GetResult().Select(m => (receiver, m)));

            return copies.Count >= lost.Count;
        });

        Assert.Equal(lost.Order(), copies.Select(c => c.Message.MessageId).Order());
        Assert.All(copies, c => Assert.Equal(2, c.Message.DeliveryCount));
        foreach (var (receiver, message) in copies)
            await receiver.CompleteAsync(message, TestCancellationToken);

        await AssertNothingReceivedAsync(first, TimeSpan.FromSeconds(1));
        await AssertNothingReceivedAsync(second, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task NewSubscription_StartsAtTheEndOfTheTopic()
    {
        string topic = NewName("late-sub");
        await using var early = await CreateReceiverAsync(MessageSource.Subscription(topic, "early"));
        await Transport.SendAsync(MessageDestination.Topic(topic), [Message("before")], TestCancellationToken);

        await using var late = await CreateReceiverAsync(MessageSource.Subscription(topic, "late"));
        await Transport.SendAsync(MessageDestination.Topic(topic), [Message("after")], TestCancellationToken);

        var message = await ReceiveOneAsync(late);
        Assert.Equal("after", Encoding.UTF8.GetString(message.Body.Span));
        await late.CompleteAsync(message, TestCancellationToken);
        await AssertNothingReceivedAsync(late, TimeSpan.FromSeconds(1));
        Assert.Equal(2, (await ReceiveAsync(early, 2)).Count);
    }

    [Fact]
    public async Task InstanceSubscription_LeavesNoConsumerGroupBehind()
    {
        string topic = NewName("no-group");
        await using (var receiver = await CreateReceiverAsync(MessageSource.InstanceSubscription(topic, "probe")))
        {
            await Transport.SendAsync(MessageDestination.Topic(topic), [Message("hello")], TestCancellationToken);
            await receiver.CompleteAsync(await ReceiveOneAsync(receiver), TestCancellationToken);
        }

        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = KafkaTestConnection.BootstrapServers }).Build();
        var groups = await admin.ListConsumerGroupsAsync();
        Assert.DoesNotContain(groups.Valid, g => g.GroupId.StartsWith($"{Prefix}.instance.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Names_DifferingOnlyByDotAndUnderscore_StaySeparate()
    {
        string suffix = Guid.NewGuid().ToString("N")[..12];
        var dotted = MessageSource.Queue($"names.{suffix}");
        var underscored = MessageSource.Queue($"names_{suffix}");
        await using var dottedReceiver = await CreateReceiverAsync(dotted);
        await using var underscoredReceiver = await CreateReceiverAsync(underscored);

        await Transport.SendAsync(MessageDestination.Queue(dotted.Name), [Message("dotted")], TestCancellationToken);
        await Transport.SendAsync(MessageDestination.Queue(underscored.Name), [Message("underscored")], TestCancellationToken);

        Assert.Equal("dotted", Encoding.UTF8.GetString((await ReceiveOneAsync(dottedReceiver)).Body.Span));
        Assert.Equal("underscored", Encoding.UTF8.GetString((await ReceiveOneAsync(underscoredReceiver)).Body.Span));
        await AssertNothingReceivedAsync(dottedReceiver, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task DeadLetter_OfAMessageAtTheSizeLimit_FitsWithALongReason()
    {
        var body = new byte[(int)(Transport.Capabilities.MaxMessageSize!.Value - MessageSize.Of("max", ReadOnlySpan<byte>.Empty, new Dictionary<string, string>()))];
        Random.Shared.NextBytes(body);
        var source = MessageSource.Queue(NewName("max-size"));
        await using var receiver = await CreateReceiverAsync(source);

        await Transport.SendAsync(MessageDestination.Queue(source.Name), [new OutgoingMessage { MessageId = "max", Body = body }], TestCancellationToken);

        var message = await ReceiveOneAsync(receiver);
        await receiver.DeadLetterAsync(message, new string('x', 10_000), TestCancellationToken);
        await using var deadLetters = await CreateReceiverAsync(MessageSource.DeadLetter(source));
        Assert.True((await ReceiveOneAsync(deadLetters)).Body.Span.SequenceEqual(body));
    }

    [Fact]
    public async Task Delete_RemovesTheSourcesTopicsAndConsumerGroups()
    {
        string topic = NewName("delete-kafka");
        var queue = MessageSource.Queue(NewName("delete-kafka"));
        var subscription = MessageSource.Subscription(topic, "gone");
        foreach (var source in new[] { queue, subscription })
        {
            await using var receiver = await CreateReceiverAsync(source);
            await receiver.DeadLetterAsync(await SendAndReceiveAsync(source, receiver), "conformance", TestCancellationToken);
            await using var deadLetters = await CreateReceiverAsync(MessageSource.DeadLetter(source));
            await deadLetters.CompleteAsync(await ReceiveOneAsync(deadLetters), TestCancellationToken);
        }

        await Transport.DeleteAsync(queue, TestCancellationToken);
        await Transport.DeleteAsync(subscription, TestCancellationToken);

        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = KafkaTestConnection.BootstrapServers }).Build();
        var topics = admin.GetMetadata(TimeSpan.FromSeconds(10)).Topics.Where(t => !t.Error.IsError).Select(t => t.Topic).ToList();
        var groups = (await admin.ListConsumerGroupsAsync()).Valid.Select(g => g.GroupId).ToList();
        Assert.DoesNotContain(topics, t => t.Contains(queue.Name, StringComparison.Ordinal));
        Assert.DoesNotContain(groups, g => g.Contains(queue.Name, StringComparison.Ordinal));
        Assert.DoesNotContain(groups, g => g.Contains(topic, StringComparison.Ordinal));
        Assert.Equal([$"{Prefix}.topic.{topic}"], topics.Where(t => t.Contains(topic, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Delete_WithAnOpenReceiver_Throws()
    {
        var source = MessageSource.Queue(NewName("delete-open"));
        await using var receiver = await CreateReceiverAsync(source);
        await SendAndReceiveAsync(source, receiver);

        await Assert.ThrowsAsync<MessageBusException>(() => Transport.DeleteAsync(source, TestCancellationToken));
    }

    [Fact]
    public Task Send_WithAReservedHeader_Throws()
    {
        var headers = new Dictionary<string, string> { ["x-foundatio-id"] = "spoofed" };
        return Assert.ThrowsAsync<ArgumentException>(() => Transport.SendAsync(MessageDestination.Queue(NewName("reserved")), [Message("x", headers: headers)], TestCancellationToken));
    }

    [Fact]
    public async Task DeliverAt_LongerThanATier_HopsAndArrivesOnTime()
    {
        var delay = TimeSpan.FromSeconds(7);
        var source = MessageSource.Queue(NewName("hops"));
        await using var receiver = await CreateReceiverAsync(source);
        var stopwatch = Stopwatch.StartNew();

        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("hop") with { DeliverAt = DateTimeOffset.UtcNow.Add(delay) }], TestCancellationToken);

        var message = await ReceiveOneAsync(receiver, delay + DeliveryTimeout);
        Assert.InRange(stopwatch.Elapsed, delay * 0.95, delay + TimeSpan.FromSeconds(5));
        await receiver.CompleteAsync(message, TestCancellationToken);
    }

    [Fact]
    public async Task DeliverAt_SurvivesTheSendingProcessStopping()
    {
        var source = MessageSource.Queue(NewName("orphan-delay"));
        var sender = new KafkaMessageTransport(CreateOptions());
        await sender.SendAsync(MessageDestination.Queue(source.Name), [Message("later") with { DeliverAt = DateTimeOffset.UtcNow.AddSeconds(3) }], TestCancellationToken);
        await sender.DisposeAsync();

        await using var receiver = await CreateReceiverAsync(source);
        var message = await ReceiveOneAsync(receiver, DeliveryTimeout);
        Assert.Equal("later", Encoding.UTF8.GetString(message.Body.Span));
        await receiver.CompleteAsync(message, TestCancellationToken);
    }

    [Fact]
    public async Task Rebalance_OfAPausedPartitionAndBack_ResumesReadingIt()
    {
        var transport = Track(new KafkaMessageTransport(CreateOptions() with
        {
            Partitions = 2,
            DelayedDelivery = false,
            RebalanceGracePeriod = TimeSpan.FromMilliseconds(500),
            ConfigureProducer = c => c.StickyPartitioningLingerMs = 0
        }));
        var source = MessageSource.Queue(NewName("paused-moved"));
        await using var first = (KafkaMessageReceiver)await transport.CreateReceiverAsync(source, new ReceiverOptions { PrefetchCount = 1 }, TestCancellationToken);
        await transport.SendAsync(MessageDestination.Queue(source.Name), Enumerable.Range(0, 40).Select(i => Message($"first-{i}")).ToList(), TestCancellationToken);
        await WaitForAsync(() => first.PausedCount == 2);

        await MovePartitionAwayAndBackAsync(transport, source, first);
        await transport.SendAsync(MessageDestination.Queue(source.Name), Enumerable.Range(0, 20).Select(i => Message($"second-{i}")).ToList(), TestCancellationToken);

        var received = await ReceiveAsync(first, 60);
        Assert.Equal(60, received.Select(m => m.MessageId).Distinct().Count());
    }

    [Fact]
    public async Task Rebalance_OfHeldDelayTierPartitionsAndBack_HoldsTheirHeadsAgain()
    {
        var options = CreateOptions() with { Prefix = UniquePrefix(), Partitions = 1 };
        var transport = Track(new KafkaMessageTransport(options));
        var source = MessageSource.Queue(NewName("tiers"));
        await using var receiver = await transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken);
        var forwarder = await transport.GetForwarderAsync();
        await WaitForAsync(() => forwarder.Assignment.Count == KafkaDelayTiers.All.Count);

        var now = DateTimeOffset.UtcNow;
        TimeSpan[] delays = [TimeSpan.FromSeconds(40), TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(11), TimeSpan.FromMinutes(61), TimeSpan.FromHours(7)];
        await transport.SendAsync(MessageDestination.Queue(source.Name), delays.Select(d => Message($"later-{d}") with { DeliverAt = now.Add(d) }).ToList(), TestCancellationToken);
        string[] longTiers = [.. new[] { TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), TimeSpan.FromHours(1), TimeSpan.FromHours(6) }.Select(t => $"{options.Prefix}.delay.{KafkaDelayTiers.Label(t)}")];
        await WaitForAsync(() => longTiers.All(forwarder.HeldTopics.Contains));

        // Only a held partition that moves away and back can be left paused; with five of seven held, the other
        // forwarder's share includes one, but it may be the 30s tier, whose head comes due during the test.
        bool movedALongTier = false;
        for (int attempt = 0; attempt < 3 && !movedALongTier; attempt++)
        {
            await using var other = new KafkaMessageTransport(options);
            await using var otherReceiver = await other.CreateReceiverAsync(MessageSource.Queue(NewName("other")), new ReceiverOptions(), TestCancellationToken);
            var otherForwarder = await other.GetForwarderAsync();
            await WaitForAsync(() => otherForwarder.Assignment.Count > 0 && forwarder.Assignment.Count + otherForwarder.Assignment.Count == KafkaDelayTiers.All.Count, TimeSpan.FromSeconds(30));
            movedALongTier = otherForwarder.Assignment.Any(p => longTiers.Contains(p.Topic));
        }

        Assert.True(movedALongTier, "No held long-tier partition moved to the other forwarder.");
        await WaitForAsync(() => forwarder.Assignment.Count == KafkaDelayTiers.All.Count, TimeSpan.FromSeconds(30));
        await WaitForAsync(() => longTiers.All(forwarder.HeldTopics.Contains));
    }

    [Fact]
    public async Task CreateReceiver_CancelledWhileJoining_LeavesNoConsumerBehind()
    {
        var transport = Track(new KafkaMessageTransport(CreateOptions() with { Partitions = 1, DelayedDelivery = false }));
        var source = MessageSource.Queue(NewName("cancelled-join"));
        await transport.SendAsync(MessageDestination.Queue(source.Name), Enumerable.Range(0, 3).Select(i => Message($"m{i}")).ToList(), TestCancellationToken);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.CreateReceiverAsync(source, new ReceiverOptions(), cancellation.Token));

        Assert.Equal(0, transport.ReceiverCount);
        await using var receiver = await transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken);
        Assert.Equal(3, (await ReceiveAsync(receiver, 3)).Count);
    }

    [Fact]
    public async Task Abandon_OfADeadLetter_LeavesItInPlace()
    {
        var transport = Track(new KafkaMessageTransport(CreateOptions() with { Partitions = 1 }));
        var source = MessageSource.Queue(NewName("peek"));
        await using (var receiver = await transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken))
        {
            await transport.SendAsync(MessageDestination.Queue(source.Name), [Message("a", "a"), Message("b", "b"), Message("c", "c")], TestCancellationToken);
            foreach (var message in await ReceiveAsync(receiver, 3))
                await receiver.DeadLetterAsync(message, "peeked", TestCancellationToken);
        }

        var peeks = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            await using var deadLetters = await transport.CreateReceiverAsync(MessageSource.DeadLetter(source), new ReceiverOptions(), TestCancellationToken);
            var message = await ReceiveOneAsync(deadLetters);
            peeks.Add(message.MessageId);
            await deadLetters.AbandonAsync(message, TimeSpan.Zero, TestCancellationToken);
        }

        Assert.Equal(["a", "a", "a"], peeks);
    }

    private async Task MovePartitionAwayAndBackAsync(KafkaMessageTransport transport, MessageSource source, KafkaMessageReceiver first)
    {
        int assigned = first.Assignment.Count;
        await using (var second = (KafkaMessageReceiver)await transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken))
            await WaitForAsync(() => second.Assignment.Count > 0 && first.Assignment.Count + second.Assignment.Count == assigned, TimeSpan.FromSeconds(30));

        await WaitForAsync(() => first.Assignment.Count == assigned, TimeSpan.FromSeconds(30));
    }

    /// <summary>A prefix of its own, for tests that need the delay forwarders' consumer group to themselves.</summary>
    private static string UniquePrefix() => $"{Prefix}-{Guid.NewGuid():N}"[..(Prefix.Length + 9)];

    private async Task<IncomingMessage> SendAndReceiveAsync(MessageSource source, IMessageReceiver receiver)
    {
        await Transport.SendAsync(MessageDestination.For(source), [Message("delete me")], TestCancellationToken);
        return await ReceiveOneAsync(receiver);
    }

    private KafkaMessageTransportOptions CreateOptions() => new()
    {
        BootstrapServers = KafkaTestConnection.BootstrapServers,
        Prefix = Prefix,
        Partitions = 3,
        ReplicationFactor = 1,
        ConfigureConsumer = KafkaTestConnection.UseConsumerGroupProtocol ? c => c.GroupProtocol = GroupProtocol.Consumer : null,
        LoggerFactory = Log
    };
}
