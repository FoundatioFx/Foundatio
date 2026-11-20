using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;
using Foundatio.Tests.Messaging;
using Microsoft.Extensions.Logging;
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
    public async Task Rebalance_CommitsWhatWasCompletedOnTheMovedPartition()
    {
        // No periodic commit happens during the test, so only releasing the partition can commit its offsets.
        var transport = Track(new KafkaMessageTransport(CreateOptions() with
        {
            Partitions = 2,
            DelayedDelivery = false,
            CommitInterval = TimeSpan.FromMinutes(5),
            ConfigureProducer = c => c.StickyPartitioningLingerMs = 0
        }));
        var source = MessageSource.Queue(NewName("revoke-commit"));
        await using var first = await transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken);
        await transport.SendAsync(MessageDestination.Queue(source.Name), Enumerable.Range(0, 20).Select(i => Message($"m{i}")).ToList(), TestCancellationToken);
        foreach (var message in await ReceiveAsync(first, 20))
            await first.CompleteAsync(message, TestCancellationToken);

        await using var second = (KafkaMessageReceiver)await transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken);
        await WaitForAsync(() => second.Assignment.Count == 1, TimeSpan.FromSeconds(30));

        await AssertNothingReceivedAsync(second, TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Rebalance_OfAPausedPartitionAndBack_ResumesReadingIt()
    {
        var transport = Track(new KafkaMessageTransport(CreateOptions() with
        {
            Partitions = 2,
            DelayedDelivery = false,
            RebalanceGracePeriod = TimeSpan.FromMilliseconds(500),
            ConfigureProducer = c => c.StickyPartitioningLingerMs = 0,
            ReceiverBufferLimit = 4
        }));
        var source = MessageSource.Queue(NewName("paused-moved"));
        await using var first = (KafkaMessageReceiver)await transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken);

        // Nothing is received, so the buffer fills, then grows past twice its limit as the receiver keeps polling.
        await transport.SendAsync(MessageDestination.Queue(source.Name), Enumerable.Range(0, 40).Select(i => Message($"first-{i}")).ToList(), TestCancellationToken);
        await WaitForAsync(() => first.PausedCount == 2);

        var received = new List<IncomingMessage>();
        await using (var second = (KafkaMessageReceiver)await transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken))
        {
            await WaitForAsync(() => first.Assignment.Count == 1 && second.Assignment.Count == 1, TimeSpan.FromSeconds(30));

            // Drain the first receiver so it no longer pauses anything when the moved partition comes back.
            await WaitForAsync(() =>
            {
                received.AddRange(first.ReceiveAsync(10, TimeSpan.FromMilliseconds(100), TestCancellationToken).GetAwaiter().GetResult());
                return first.PausedCount == 0;
            });
        }

        await WaitForAsync(() => first.Assignment.Count == 2, TimeSpan.FromSeconds(30));
        await transport.SendAsync(MessageDestination.Queue(source.Name), Enumerable.Range(0, 20).Select(i => Message($"second-{i}")).ToList(), TestCancellationToken);

        received.AddRange(await ReceiveAsync(first, 60 - received.Count));
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

    [Fact]
    public async Task DeliverAt_WithATargetOutsideTheTransportsTopics_IsNotForwarded()
    {
        var options = CreateOptions() with { Prefix = UniquePrefix(), Partitions = 1 };
        var transport = Track(new KafkaMessageTransport(options));
        var source = MessageSource.Queue(NewName("foreign"));
        await using var receiver = await transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken);
        await transport.GetForwarderAsync();
        string foreign = NewName("foundatio-tests-foreign");
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = KafkaTestConnection.BootstrapServers }).Build();
        await admin.CreateTopicsAsync([new TopicSpecification { Name = foreign, NumPartitions = 1, ReplicationFactor = 1 }]);
        try
        {
            // Anyone who can write to the delay topics could name any target; only the transport's own are forwarded.
            using (var producer = new ProducerBuilder<byte[]?, byte[]>(new ProducerConfig { BootstrapServers = KafkaTestConnection.BootstrapServers }).Build())
            {
                await producer.ProduceAsync($"{options.Prefix}.delay.1s", new Message<byte[]?, byte[]>
                {
                    Value = Encoding.UTF8.GetBytes("forged"),
                    Headers = new Headers
                    {
                        { "x-foundatio-id", Encoding.UTF8.GetBytes("forged") },
                        { "x-foundatio-target", Encoding.UTF8.GetBytes(foreign) },
                        { "x-foundatio-deliver-at", Encoding.UTF8.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString()) }
                    }
                }, TestCancellationToken);
            }

            await transport.SendAsync(MessageDestination.Queue(source.Name), [Message("genuine") with { DeliverAt = DateTimeOffset.UtcNow.AddSeconds(1) }], TestCancellationToken);
            Assert.Equal("genuine", Encoding.UTF8.GetString((await ReceiveOneAsync(receiver)).Body.Span));

            using var consumer = new ConsumerBuilder<Ignore, Ignore>(new ConsumerConfig { BootstrapServers = KafkaTestConnection.BootstrapServers, GroupId = foreign }).Build();
            Assert.Equal(0, consumer.QueryWatermarkOffsets(new TopicPartition(foreign, 0), TimeSpan.FromSeconds(10)).High.Value);
        }
        finally
        {
            await admin.DeleteTopicsAsync([foreign]);
        }
    }

    [Fact]
    public async Task DeliverAt_ToATargetThatRejectsTheRecord_IsDroppedWithoutHoldingBackItsTier()
    {
        var options = CreateOptions() with { Prefix = UniquePrefix(), Partitions = 1, DelayForwardRetryDelay = TimeSpan.FromMilliseconds(100), DelayForwardMaxAttempts = 3 };
        var source = MessageSource.Queue(NewName("too-large"));
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = KafkaTestConnection.BootstrapServers }).Build();
        await admin.CreateTopicsAsync([new TopicSpecification
        {
            Name = $"{options.Prefix}.queue.{source.Name}",
            NumPartitions = 1,
            ReplicationFactor = 1,
            Configs = new Dictionary<string, string> { ["max.message.bytes"] = "4096" }
        }]);

        // The delay tiers take the record, but the queue it is due on takes only small ones.
        var first = new KafkaMessageTransport(options);
        await using (var receiver = await first.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken))
        {
            var due = DateTimeOffset.UtcNow.AddSeconds(1.5);
            await first.SendAsync(MessageDestination.Queue(source.Name), [new OutgoingMessage { MessageId = "large", Body = new byte[20_000], DeliverAt = due }], TestCancellationToken);
            await first.SendAsync(MessageDestination.Queue(source.Name), [Message("small", "small") with { DeliverAt = due }], TestCancellationToken);

            var message = await ReceiveOneAsync(receiver);
            Assert.Equal("small", message.MessageId);
            await receiver.CompleteAsync(message, TestCancellationToken);
            var forwarder = await first.GetForwarderAsync();
            await WaitForAsync(() => forwarder.PendingCount == 0);
        }

        await first.DisposeAsync();

        // A forwarder that takes over the tier starts after both records instead of forwarding the small one again.
        var second = Track(new KafkaMessageTransport(options));
        await using var again = await second.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken);
        await second.GetForwarderAsync();
        await AssertNothingReceivedAsync(again, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DeliverAt_WhenTheDelayForwarderFails_IsDeliveredByTheRestartedForwarder()
    {
        var transport = Track(new KafkaMessageTransport(CreateOptions() with { Prefix = UniquePrefix(), Partitions = 1 }));
        var source = MessageSource.Queue(NewName("forwarder-restart"));
        await using var receiver = await transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken);
        var forwarder = await transport.GetForwarderAsync();
        await transport.SendAsync(MessageDestination.Queue(source.Name), [Message("later") with { DeliverAt = DateTimeOffset.UtcNow.AddSeconds(3) }], TestCancellationToken);

        forwarder.FailForTesting(new KafkaException(ErrorCode.Local_Fatal));

        var message = await ReceiveOneAsync(receiver, TimeSpan.FromSeconds(45));
        Assert.Equal("later", Encoding.UTF8.GetString(message.Body.Span));
    }

    [Fact]
    public async Task CreateTopics_WithShortRetentionOrCompaction_KeepsTheDelayAndDeadLetterTopicsSafe()
    {
        var options = CreateOptions() with
        {
            Prefix = UniquePrefix(),
            TopicConfigs = new Dictionary<string, string> { ["retention.ms"] = "60000", ["retention.bytes"] = "1048576", ["cleanup.policy"] = "compact" }
        };
        var transport = Track(new KafkaMessageTransport(options));
        var source = MessageSource.Queue(NewName("retention"));
        await using (await transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken))
        await using (await transport.CreateReceiverAsync(MessageSource.DeadLetter(source), new ReceiverOptions(), TestCancellationToken))
        {
        }

        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = KafkaTestConnection.BootstrapServers }).Build();
        async Task<Dictionary<string, string>> DescribeAsync(string topic)
        {
            var result = await admin.DescribeConfigsAsync([new ConfigResource { Type = ResourceType.Topic, Name = topic }]);
            return result.Single().Entries.ToDictionary(e => e.Key, e => e.Value.Value);
        }

        var queue = await DescribeAsync($"{options.Prefix}.queue.{source.Name}");
        Assert.Equal("60000", queue["retention.ms"]);
        Assert.Equal("compact", queue["cleanup.policy"]);

        var deadLetters = await DescribeAsync($"{options.Prefix}.deadletter.queue.{source.Name}");
        Assert.Equal("delete", deadLetters["cleanup.policy"]);
        Assert.Equal(TimeSpan.FromDays(14).TotalMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture), deadLetters["retention.ms"]);
        Assert.Equal("-1", deadLetters["retention.bytes"]);

        foreach (var tier in KafkaDelayTiers.All)
        {
            var delay = await DescribeAsync($"{options.Prefix}.delay.{KafkaDelayTiers.Label(tier)}");
            Assert.Equal("delete", delay["cleanup.policy"]);
            Assert.Equal("LogAppendTime", delay["message.timestamp.type"]);
            Assert.Equal(TimeSpan.FromDays(7).TotalMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture), delay["retention.ms"]);
            Assert.Equal("-1", delay["retention.bytes"]);
        }
    }

    [Fact]
    public async Task Receive_FromABacklogWithSlowHandlers_KeepsUpWithTheHandlers()
    {
        var transport = Track(new KafkaMessageTransport(CreateOptions() with { Partitions = 1, DelayedDelivery = false }));
        var source = MessageSource.Queue(NewName("backlog"));
        const int count = 3000;
        var body = new byte[1000];
        for (int i = 0; i < count; i += 1000)
            await transport.SendAsync(MessageDestination.Queue(source.Name), Enumerable.Range(i, 1000).Select(n => new OutgoingMessage { MessageId = n.ToString(), Body = body }).ToList(), TestCancellationToken);

        await using var receiver = await transport.CreateReceiverAsync(source, new ReceiverOptions { PrefetchCount = 8 }, TestCancellationToken);
        var stopwatch = Stopwatch.StartNew();
        int received = 0;
        while (received < count && stopwatch.Elapsed < TimeSpan.FromSeconds(60))
        {
            // Handlers that take 5ms per batch of 16 could handle 3,200 messages a second.
            var batch = await receiver.ReceiveAsync(16, TimeSpan.FromSeconds(1), TestCancellationToken);
            await Task.Delay(5, TestCancellationToken);
            foreach (var message in batch)
                await receiver.CompleteAsync(message, TestCancellationToken);

            received += batch.Count;
        }

        _logger.LogInformation("Received {Count} messages in {Elapsed} ({Rate:0} a second)", received, stopwatch.Elapsed, received / stopwatch.Elapsed.TotalSeconds);
        Assert.Equal(count, received);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15), $"Receiving the backlog took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task Receive_WithAGroupBacklogHeldBehindAnUnsettledMessage_DeliversOtherGroupsOfItsPartition()
    {
        var transport = Track(new KafkaMessageTransport(CreateOptions() with { Partitions = 1, DelayedDelivery = false, ReceiverBufferLimit = 8 }));
        var source = MessageSource.Queue(NewName("hot-group"));
        await using var receiver = await transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken);
        await transport.SendAsync(MessageDestination.Queue(source.Name), Enumerable.Range(0, 100).Select(i => Message($"hot-{i}", $"hot-{i}") with { GroupKey = "hot" }).ToList(), TestCancellationToken);
        await transport.SendAsync(MessageDestination.Queue(source.Name), Enumerable.Range(0, 20).Select(i => Message($"cold-{i}", $"cold-{i}") with { GroupKey = $"cold-{i}" }).ToList(), TestCancellationToken);

        // The hot group's first message is still being handled, so the rest of its backlog waits behind it.
        var hot = await ReceiveOneAsync(receiver);
        Assert.Equal("hot-0", hot.MessageId);

        var cold = await ReceiveAsync(receiver, 20);
        Assert.Equal(Enumerable.Range(0, 20).Select(i => $"cold-{i}").Order(), cold.Select(m => m.MessageId).Order());
        foreach (var message in cold)
            await receiver.CompleteAsync(message, TestCancellationToken);

        await receiver.CompleteAsync(hot, TestCancellationToken);
        var order = new List<string> { hot.MessageId };
        order.AddRange(await DrainAsync(receiver, 99));
        Assert.Equal(Enumerable.Range(0, 100).Select(i => $"hot-{i}"), order);
    }

    [Fact]
    public async Task Receive_WithAGroupBacklogOnOnePartition_DeliversOtherGroupsWhileItsHeadIsUnsettled()
    {
        var transport = Track(new KafkaMessageTransport(CreateOptions() with { Partitions = 1, DelayedDelivery = false, ReceiverBufferLimit = 8, ReceiverGroupHeldLimit = 10 }));
        var source = MessageSource.Queue(NewName("skip-hot"));
        var destination = MessageDestination.Queue(source.Name);
        await using var receiver = (KafkaMessageReceiver)await transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken);
        await transport.SendAsync(destination, Enumerable.Range(0, 100).Select(i => Message($"hot-{i}", $"hot-{i}") with { GroupKey = "hot" }).ToList(), TestCancellationToken);
        await transport.SendAsync(destination, Enumerable.Range(0, 20).Select(i => Message($"cold-{i}", $"cold-{i}") with { GroupKey = $"cold-{i}" }).ToList(), TestCancellationToken);

        // The hot group's first message is still being handled. Once it holds its budget of 10 behind it and does not
        // drain, its later records are skipped, so the groups behind them on the same partition are read and delivered.
        var hot = (KafkaIncomingMessage)await ReceiveOneAsync(receiver);
        Assert.Equal("hot-0", hot.MessageId);
        var cold = await ReceiveAsync(receiver, 20);
        Assert.Equal(Enumerable.Range(0, 20).Select(i => $"cold-{i}").Order(), cold.Select(m => m.MessageId).Order());
        foreach (var message in cold)
            await receiver.CompleteAsync(message, TestCancellationToken);

        Assert.Equal(0, receiver.Pauses);
        Assert.Equal(0, receiver.HeldStops);
        Assert.Equal(1, receiver.GroupStops);
        int skipped = receiver.SkippedCount;
        Assert.InRange(skipped, 89, 90);
        Assert.Equal(skipped, receiver.SkippedTotal);
        Assert.Equal(hot.Offset + 100 - skipped, receiver.LowestSkippedOffset(hot.Partition.TopicPartition));

        // Once its first message is settled, the group drains, and its skipped records are read again, once, in order.
        await receiver.CompleteAsync(hot, TestCancellationToken);
        var order = new List<string> { hot.MessageId };
        order.AddRange(await DrainAsync(receiver, 99));
        Assert.Equal(Enumerable.Range(0, 100).Select(i => $"hot-{i}"), order);
        Assert.Equal(skipped, receiver.ReadAgainCount);
        Assert.Equal(0, receiver.SkippedCount);
        Assert.Equal(0, receiver.Pauses);

        // With nothing skipped, the catch-up reader gives up its partitions and reads nothing.
        await WaitForAsync(() => receiver.CatchUpReader is { AssignedCount: 0 });
        long reads = receiver.CatchUpReader!.Reads;
        Assert.Equal(skipped, reads);
        await AssertNothingReceivedAsync(receiver, TimeSpan.FromSeconds(1));
        Assert.Equal(reads, receiver.CatchUpReader.Reads);
    }

    [Fact]
    public async Task Receive_WithASkippingGroup_DeliversItsRecordsOnceInSendOrder()
    {
        var transport = Track(new KafkaMessageTransport(CreateOptions() with { Partitions = 1, DelayedDelivery = false, ReceiverBufferLimit = 8, ReceiverGroupHeldLimit = 10 }));
        var source = MessageSource.Queue(NewName("skip-order"));
        var destination = MessageDestination.Queue(source.Name);
        await using var receiver = (KafkaMessageReceiver)await transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken);

        // Two runs of the hot group, each followed by more of other groups than the reader reads through to its next record.
        await transport.SendAsync(destination, Enumerable.Range(0, 50).Select(i => Message($"hot-{i}", $"hot-{i}") with { GroupKey = "hot" }).ToList(), TestCancellationToken);
        await transport.SendAsync(destination, Enumerable.Range(0, 200).Select(i => Message($"cold-{i}", $"cold-{i}") with { GroupKey = $"cold-{i}" }).ToList(), TestCancellationToken);
        await transport.SendAsync(destination, Enumerable.Range(50, 50).Select(i => Message($"hot-{i}", $"hot-{i}") with { GroupKey = "hot" }).ToList(), TestCancellationToken);
        await transport.SendAsync(destination, Enumerable.Range(200, 200).Select(i => Message($"cold-{i}", $"cold-{i}") with { GroupKey = $"cold-{i}" }).ToList(), TestCancellationToken);

        var first = (KafkaIncomingMessage)await ReceiveOneAsync(receiver);
        Assert.Equal("hot-0", first.MessageId);
        var cold = await DrainAsync(receiver, 400, TimeSpan.FromSeconds(60));
        Assert.Equal(Enumerable.Range(0, 400).Select(i => $"cold-{i}").Order(), cold.Order());
        Assert.True(receiver.SkippedCount > 50, $"Skipped {receiver.SkippedCount}.");

        // The group is handled slowly, so its skipped records are read again over several rounds, each up to its budget.
        await receiver.CompleteAsync(first, TestCancellationToken);
        var hot = new List<KafkaIncomingMessage> { first };
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (hot.Count < 100 && DateTime.UtcNow < deadline)
        {
            var batch = await receiver.ReceiveAsync(4, TimeSpan.FromSeconds(1), TestCancellationToken);
            await Task.Delay(20, TestCancellationToken);
            foreach (var message in batch)
            {
                hot.Add((KafkaIncomingMessage)message);
                await receiver.CompleteAsync(message, TestCancellationToken);
            }
        }

        Assert.Equal(Enumerable.Range(0, 100).Select(i => $"hot-{i}"), hot.Select(m => m.MessageId));
        Assert.Equal(Enumerable.Range(0, 50).Select(i => first.Offset + i).Concat(Enumerable.Range(50, 50).Select(i => first.Offset + 200 + i)), hot.Select(m => m.Offset));
        Assert.All(hot, m => Assert.Equal(1, m.DeliveryCount));
        await AssertNothingReceivedAsync(receiver, TimeSpan.FromSeconds(1));

        Assert.Equal(receiver.SkippedTotal, receiver.ReadAgainCount);
        Assert.Equal(0, receiver.SkippedCount);
        Assert.Equal(0, receiver.Pauses);
        var reader = receiver.CatchUpReader!;
        Assert.Equal(receiver.SkippedTotal, reader.Reads);
        Assert.Equal(2, reader.Seeks);
        await WaitForAsync(() => reader.AssignedCount == 0);
    }

    [Fact]
    public async Task Receive_WithAGroupHeadWaitingOutARetryDelay_SkipsWithoutStoppingTheReading()
    {
        var transport = Track(new KafkaMessageTransport(CreateOptions() with { Partitions = 1, DelayedDelivery = false, ReceiverBufferLimit = 8, ReceiverGroupHeldLimit = 10 }));
        var source = MessageSource.Queue(NewName("skip-retry"));
        var destination = MessageDestination.Queue(source.Name);
        await using var receiver = (KafkaMessageReceiver)await transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken);
        await transport.SendAsync(destination, [Message("hot-0", "hot-0") with { GroupKey = "hot" }], TestCancellationToken);
        var retryAt = DateTimeOffset.UtcNow.AddSeconds(8);
        await receiver.AbandonAsync(await ReceiveOneAsync(receiver), TimeSpan.FromSeconds(8), TestCancellationToken);

        // The group's records are held behind its first, which waits out its retry delay, so it cannot drain soon: once it
        // holds its budget its later records are skipped at once, without stopping the reading first.
        await transport.SendAsync(destination, Enumerable.Range(1, 40).Select(i => Message($"hot-{i}", $"hot-{i}") with { GroupKey = "hot" }).ToList(), TestCancellationToken);
        await transport.SendAsync(destination, Enumerable.Range(0, 20).Select(i => Message($"cold-{i}", $"cold-{i}") with { GroupKey = $"cold-{i}" }).ToList(), TestCancellationToken);
        var cold = await DrainAsync(receiver, 20);
        Assert.True(DateTimeOffset.UtcNow < retryAt, "The other groups were not delivered before the retry was due.");
        Assert.Equal(Enumerable.Range(0, 20).Select(i => $"cold-{i}").Order(), cold.Order());
        Assert.Equal(0, receiver.GroupStops);
        Assert.Equal(0, receiver.HeldStops);
        Assert.Equal(0, receiver.Pauses);
        Assert.Equal(30, receiver.SkippedCount);

        var retried = await ReceiveOneAsync(receiver, TimeSpan.FromSeconds(30));
        Assert.Equal("hot-0", retried.MessageId);
        Assert.Equal(2, retried.DeliveryCount);
        await receiver.CompleteAsync(retried, TestCancellationToken);
        Assert.Equal(Enumerable.Range(1, 40).Select(i => $"hot-{i}"), await DrainAsync(receiver, 40));
        Assert.Equal(30, receiver.ReadAgainCount);
    }

    [Fact]
    public async Task Receive_WithAGroupBacklogReadWhileItsPartitionLoads_SkipsWhatItHoldsPastItsBudgetOnceItHasNotDrained()
    {
        var transport = Track(new KafkaMessageTransport(CreateOptions() with
        {
            Partitions = 1,
            DelayedDelivery = false,
            ReceiverBufferLimit = 8,
            ReceiverHeldLimit = 50,
            ReceiverGroupHeldLimit = 10
        }));
        var source = MessageSource.Queue(NewName("skip-loaded"));
        var destination = MessageDestination.Queue(source.Name);
        await transport.SendAsync(destination, Enumerable.Range(0, 300).Select(i => Message($"hot-{i}", $"hot-{i}") with { GroupKey = "hot" }).ToList(), TestCancellationToken);
        await transport.SendAsync(destination, Enumerable.Range(0, 20).Select(i => Message($"cold-{i}", $"cold-{i}") with { GroupKey = $"cold-{i}" }).ToList(), TestCancellationToken);

        // While the partition's state loads, nothing can be handed out, so the group is not judged then: its records are
        // held, up to the partition's limit. Once loaded, the group still does not drain, so it starts skipping, and
        // skips what it held past its budget too, which lets the partition be read on to the other groups.
        await using var receiver = (KafkaMessageReceiver)await transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken);
        var hot = await ReceiveOneAsync(receiver);
        Assert.Equal("hot-0", hot.MessageId);
        var cold = await DrainAsync(receiver, 20);
        Assert.Equal(Enumerable.Range(0, 20).Select(i => $"cold-{i}").Order(), cold.Order());
        Assert.InRange(receiver.HeldCount, 9, 10);
        Assert.InRange(receiver.SkippedTotal, 289, 290);

        await receiver.CompleteAsync(hot, TestCancellationToken);
        var order = new List<string> { hot.MessageId };
        order.AddRange(await DrainAsync(receiver, 299, TimeSpan.FromSeconds(60)));
        Assert.Equal(Enumerable.Range(0, 300).Select(i => $"hot-{i}"), order);
        Assert.Equal(receiver.SkippedTotal, receiver.ReadAgainCount);
        await WaitForAsync(() => receiver.PausedCount == 0);
    }

    [Fact]
    public async Task Receive_WithAGroupBacklogStartingToSkipNearItsPartitionsSkippedLimit_SkipsAllOfItAndKeepsTheGroupInOrder()
    {
        Log.MaxLogEntriesToStore = 10_000;
        var transport = Track(new KafkaMessageTransport(CreateOptions() with
        {
            Partitions = 1,
            DelayedDelivery = false,
            ReceiverBufferLimit = 8,
            ReceiverGroupHeldLimit = 10,
            ReceiverSkippedLimit = 8,
            ReceiverHeldStopTimeout = TimeSpan.FromSeconds(1)
        }));
        var source = MessageSource.Queue(NewName("skip-near-limit"));
        var destination = MessageDestination.Queue(source.Name);
        await using var receiver = (KafkaMessageReceiver)await transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken);

        // Another group's first message is being handled: it holds its budget of 10 and skips the 5 after, of the
        // partition's 8.
        await transport.SendAsync(destination, Enumerable.Range(0, 16).Select(i => Message($"other-{i}", $"other-{i}") with { GroupKey = "other" }).ToList(), TestCancellationToken);
        Assert.Equal("other-0", (await ReceiveOneAsync(receiver)).MessageId);
        await WaitForAsync(() => receiver.SkippedCount == 5);

        // The group handles a batch of 9 while 9 more wait behind it, under its budget.
        await transport.SendAsync(destination, Enumerable.Range(0, 9).Select(i => Message($"g-{i}", $"g-{i}") with { GroupKey = "g" }).ToList(), TestCancellationToken);
        await WaitForAsync(() => receiver.HeldCount == 10 + 8);
        var batch = await receiver.ReceiveAsync(9, TimeSpan.FromSeconds(5), TestCancellationToken);
        Assert.Equal(Enumerable.Range(0, 9).Select(i => $"g-{i}"), batch.Select(m => m.MessageId));
        await transport.SendAsync(destination, Enumerable.Range(9, 9).Select(i => Message($"g-{i}", $"g-{i}") with { GroupKey = "g" }).ToList(), TestCancellationToken);
        await WaitForAsync(() => receiver.HeldCount == 10 + 9);
        Assert.Equal(1, receiver.GroupSkips);

        // The batch is released for an immediate retry, as Foundatio releases the rest of a group's batch behind a
        // failure, so the group holds 18 and does not drain. Skipping what it holds past its budget takes the partition
        // past its limit; all of it is skipped, so none of it is delivered ahead of the rest.
        foreach (var message in batch)
            await receiver.AbandonAsync(message, TimeSpan.Zero, TestCancellationToken);

        await WaitForAsync(() => receiver.GroupSkips == 2);
        int skipped = receiver.SkippedCount;
        int held = receiver.HeldCount;
        Assert.Equal(Enumerable.Range(0, 18).Select(i => $"g-{i}"), await DrainAsync(receiver, 18, TimeSpan.FromSeconds(60)));
        Assert.Equal(5 + 8, skipped);
        Assert.Equal(10 + 9, held);
        Assert.Equal(8, receiver.ReadAgainCount);

        // The warning names both the group that skipped the most and the one that skipped the oldest record.
        var warning = Assert.Single(Log.LogEntries, e => e.LogLevel == LogLevel.Warning && e.Message.Contains("records of", StringComparison.Ordinal));
        Assert.Contains("skipped 13 records", warning.Message, StringComparison.Ordinal);
        Assert.Contains("8 of group g,", warning.Message, StringComparison.Ordinal);
        Assert.Contains("the oldest of group other", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Receive_WithAGroupBacklogOlderThanItsPartitionsSkippedSpanAllows_HoldsAllOfItAndKeepsTheGroupInOrder()
    {
        var transport = Track(new KafkaMessageTransport(CreateOptions() with
        {
            Partitions = 1,
            DelayedDelivery = false,
            ReceiverBufferLimit = 8,
            ReceiverGroupHeldLimit = 10,
            ReceiverSkippedSpanLimit = 25,
            ReceiverHeldStopTimeout = TimeSpan.FromSeconds(1)
        }));
        var source = MessageSource.Queue(NewName("skip-near-span"));
        var destination = MessageDestination.Queue(source.Name);
        await using var receiver = (KafkaMessageReceiver)await transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken);

        // The group handles a batch of 9 while 9 more wait behind it, under its budget.
        await transport.SendAsync(destination, Enumerable.Range(0, 9).Select(i => Message($"g-{i}", $"g-{i}") with { GroupKey = "g" }).ToList(), TestCancellationToken);
        await WaitForAsync(() => receiver.HeldCount == 8);
        var batch = await receiver.ReceiveAsync(9, TimeSpan.FromSeconds(5), TestCancellationToken);
        Assert.Equal(Enumerable.Range(0, 9).Select(i => $"g-{i}"), batch.Select(m => m.MessageId));
        await transport.SendAsync(destination, Enumerable.Range(9, 9).Select(i => Message($"g-{i}", $"g-{i}") with { GroupKey = "g" }).ToList(), TestCancellationToken);
        await WaitForAsync(() => receiver.HeldCount == 9);

        // Then another group's first message is being handled: it holds its budget of 10 and skips the 12 after, which
        // are 12 offsets back, of the 25 the partition allows.
        await transport.SendAsync(destination, Enumerable.Range(0, 23).Select(i => Message($"other-{i}", $"other-{i}") with { GroupKey = "other" }).ToList(), TestCancellationToken);
        var other = (KafkaIncomingMessage)await ReceiveOneAsync(receiver);
        Assert.Equal("other-0", other.MessageId);
        await WaitForAsync(() => receiver.SkippedCount == 12);
        long lowest = receiver.LowestSkippedOffset(other.Partition.TopicPartition);
        Assert.Equal(other.Offset + 11, lowest);
        Assert.Equal(1, receiver.GroupSkips);

        // The batch is released for an immediate retry, so the group holds 18 and does not drain. What it holds past its
        // budget is older than the partition's skipped records, further back than the partition allows: it holds all of
        // it instead of skipping part of it, so none of it is delivered ahead of the rest.
        foreach (var message in batch)
            await receiver.AbandonAsync(message, TimeSpan.Zero, TestCancellationToken);

        await WaitForAsync(() => receiver.GroupSkips == 2);
        int skipped = receiver.SkippedCount;
        long lowestAfter = receiver.LowestSkippedOffset(other.Partition.TopicPartition);
        Assert.Equal(Enumerable.Range(0, 18).Select(i => $"g-{i}"), await DrainAsync(receiver, 18, TimeSpan.FromSeconds(60)));
        Assert.Equal(12, skipped);
        Assert.Equal(lowest, lowestAfter);
        Assert.Equal(0, receiver.ReadAgainCount);
    }

    [Fact]
    public async Task Receive_WithAGroupThatDrainsWithinTheStopTimeout_DoesNotSkip()
    {
        var transport = Track(new KafkaMessageTransport(CreateOptions() with
        {
            Partitions = 1,
            DelayedDelivery = false,
            ReceiverBufferLimit = 8,
            ReceiverGroupHeldLimit = 20,
            ReceiverHeldStopTimeout = TimeSpan.FromSeconds(30)
        }));
        var source = MessageSource.Queue(NewName("skip-drain"));
        await transport.SendAsync(MessageDestination.Queue(source.Name), Enumerable.Range(0, 300).Select(i => Message($"hot-{i}", $"hot-{i}") with { GroupKey = "hot" }).ToList(), TestCancellationToken);
        await using var receiver = (KafkaMessageReceiver)await transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken);

        // While a batch of the group is unsettled, its next records are held until they reach its budget, which stops the
        // reading. Each batch is settled well within the stop timeout, so the group drains and nothing is skipped.
        var order = new List<string>();
        for (int reached = 1; reached <= 5; reached++)
        {
            var batch = await receiver.ReceiveAsync(15, TimeSpan.FromSeconds(5), TestCancellationToken);
            await WaitForAsync(() => receiver.GroupStops >= reached);
            foreach (var message in batch)
            {
                order.Add(message.MessageId);
                await receiver.CompleteAsync(message, TestCancellationToken);
            }
        }

        order.AddRange(await DrainAsync(receiver, 300 - order.Count, TimeSpan.FromSeconds(60)));
        Assert.Equal(Enumerable.Range(0, 300).Select(i => $"hot-{i}"), order);
        Assert.Equal(0, receiver.SkippedTotal);
        Assert.Equal(0, receiver.HeldStops);
        Assert.Equal(0, receiver.Pauses);
        Assert.Null(receiver.CatchUpReader);
    }

    [Fact]
    public async Task Rebalance_OfAPartitionWithSkippedRecords_NextOwnerDeliversThemInOrderWithoutRedeliveringFinishedOnes()
    {
        var transport = Track(new KafkaMessageTransport(CreateOptions() with
        {
            Partitions = 1,
            DelayedDelivery = false,
            RebalanceGracePeriod = TimeSpan.FromMilliseconds(500),
            ReceiverBufferLimit = 8,
            ReceiverGroupHeldLimit = 10
        }));
        var source = MessageSource.Queue(NewName("skip-moved"));
        var destination = MessageDestination.Queue(source.Name);
        var first = (KafkaMessageReceiver)await transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken);
        await transport.SendAsync(destination, Enumerable.Range(0, 60).Select(i => Message($"hot-{i}", $"hot-{i}") with { GroupKey = "hot" }).ToList(), TestCancellationToken);
        await transport.SendAsync(destination, Enumerable.Range(0, 40).Select(i => Message($"cold-{i}", $"cold-{i}") with { GroupKey = $"cold-{i}" }).ToList(), TestCancellationToken);

        Assert.Equal("hot-0", (await ReceiveOneAsync(first)).MessageId);
        Assert.Equal(40, (await DrainAsync(first, 40)).Count);
        Assert.InRange(first.SkippedCount, 49, 50);

        // The partition moves to a receiver that joined meanwhile, with the hot group's first message unsettled, ten of
        // its records held and the rest skipped.
        await using var second = (KafkaMessageReceiver)await transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken);
        await first.DisposeAsync();
        await WaitForAsync(() => second.Assignment.Count == 1, TimeSpan.FromSeconds(30));

        // It delivers the group from its first message, which counts another delivery, in order and once, and none of
        // the finished messages of the other groups again.
        var again = await ReceiveOneAsync(second, TimeSpan.FromSeconds(30));
        Assert.Equal("hot-0", again.MessageId);
        Assert.Equal(2, again.DeliveryCount);
        await second.CompleteAsync(again, TestCancellationToken);
        Assert.Equal(Enumerable.Range(1, 59).Select(i => $"hot-{i}"), await DrainAsync(second, 59, TimeSpan.FromSeconds(60)));
        await AssertNothingReceivedAsync(second, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Rebalance_OfAPartitionWhoseSkippedRecordsAreBeingReadAgain_StopsReadingItForTheGracePeriod()
    {
        var transport = Track(new KafkaMessageTransport(CreateOptions() with { Partitions = 1, DelayedDelivery = false, ReceiverBufferLimit = 256, ReceiverGroupHeldLimit = 10 }));
        var source = MessageSource.Queue(NewName("skip-revoking"));
        var destination = MessageDestination.Queue(source.Name);
        await using var receiver = (KafkaMessageReceiver)await transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken);

        // Each of the hot group's records is followed by 50 of other groups, fewer than the reader seeks over, so it reads
        // through them to the group's next record.
        const int hotCount = 400;
        const int gap = 50;
        var messages = Enumerable.Range(0, hotCount).SelectMany(i => Enumerable.Range(0, gap).Select(n => Message($"cold-{i}-{n}", $"cold-{i}-{n}")).Prepend(Message($"hot-{i}", $"hot-{i}") with { GroupKey = "hot" })).ToList();
        foreach (var chunk in messages.Chunk(1000))
            await transport.SendAsync(destination, chunk, TestCancellationToken);
        await transport.SendAsync(destination, [Message("pin", "pin")], TestCancellationToken);

        // The hot group's first message is being handled, so its later records are skipped and the rest delivered. The
        // last one stays unsettled, so a revocation waits out its whole grace period.
        var hot = await ReceiveOneAsync(receiver);
        Assert.Equal("hot-0", hot.MessageId);
        KafkaIncomingMessage? pin = null;
        int cold = 0;
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (cold < hotCount * gap || pin is null)
        {
            Assert.True(DateTime.UtcNow < deadline, $"Received {cold} of {hotCount * gap} messages of other groups.");
            foreach (var message in await receiver.ReceiveAsync(100, TimeSpan.FromSeconds(1), TestCancellationToken))
            {
                if (message.MessageId == "pin")
                {
                    pin = (KafkaIncomingMessage)message;
                    continue;
                }

                cold++;
                await receiver.CompleteAsync(message, TestCancellationToken);
            }
        }

        // The group drains as fast as the reader reads its skipped records again, so the reader keeps reading.
        await receiver.CompleteAsync(hot, TestCancellationToken);
        using var draining = CancellationTokenSource.CreateLinkedTokenSource(TestCancellationToken);
        var drain = Task.Run(async () =>
        {
            while (true)
            {
                foreach (var message in await receiver.ReceiveAsync(10, TimeSpan.FromMilliseconds(100), draining.Token))
                {
                    try
                    {
                        await receiver.CompleteAsync(message, draining.Token);
                    }
                    catch (MessageLeaseLostException)
                    {
                    }
                }
            }
        }, draining.Token);
        Assert.True(SpinWait.SpinUntil(() => receiver.ReadAgainCount >= 20, TimeSpan.FromSeconds(30)));

        // A rebalance takes the partition away meanwhile. Once the reader finds it revoking it stops reading it, instead
        // of reading on through the grace period towards the end of the partition: it read the group's records it kept
        // and those of other groups between them, and at most a record or two more.
        receiver.Release([pin.Partition.TopicPartition], TimeSpan.FromSeconds(2));
        await draining.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => drain);

        var reader = receiver.CatchUpReader!;
        long kept = receiver.ReadAgainCount;
        _logger.LogInformation("The reader read {Reads} records and kept {Kept} of {Skipped} skipped", reader.Reads, kept, receiver.SkippedTotal);
        Assert.InRange(reader.Reads, kept, (gap + 1) * kept + 2);
        await WaitForAsync(() => reader.AssignedCount == 0);
    }

    [Fact]
    public async Task Commit_WithSkippedRecordsOutstanding_StopsAtTheOldestUnfinishedAndKeepsTheFinishedOnesInTheState()
    {
        var options = CreateOptions() with { Partitions = 1, DelayedDelivery = false, ReceiverGroupHeldLimit = 10 };
        var transport = Track(new KafkaMessageTransport(options));
        var source = MessageSource.Queue(NewName("skip-crash"));
        var destination = MessageDestination.Queue(source.Name);
        await using var receiver = (KafkaMessageReceiver)await transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken);
        await transport.SendAsync(destination, Enumerable.Range(0, 30).Select(i => Message($"hot-{i}", $"hot-{i}") with { GroupKey = "hot" }).ToList(), TestCancellationToken);
        for (int i = 0; i < 1100; i += 550)
            await transport.SendAsync(destination, Enumerable.Range(i, 550).Select(n => Message($"cold-{n}", $"cold-{n}")).ToList(), TestCancellationToken);

        var hot = (KafkaIncomingMessage)await ReceiveOneAsync(receiver);
        Assert.Equal(1100, (await DrainAsync(receiver, 1100, TimeSpan.FromSeconds(60))).Count);
        long lowestSkipped = receiver.LowestSkippedOffset(hot.Partition.TopicPartition);
        Assert.InRange(lowestSkipped, hot.Offset + 10, hot.Offset + 11);

        // What a crashed process leaves its partition's next owner: a committed offset at the oldest unfinished record,
        // and a state that finishes the other groups' messages behind it but none of the hot group's.
        string queue = $"{options.Prefix}.queue.{source.Name}";
        await using var other = new KafkaMessageTransport(options);
        using var offsets = other.CreateOffsetReader(queue);
        KafkaReceiverState? state = null;
        long committed = -1;
        await WaitForAsync(() =>
        {
            committed = other.GetCommittedOffsetsAsync(offsets, [hot.Partition.TopicPartition], TimeSpan.FromSeconds(5), TestCancellationToken).GetAwaiter().GetResult().Single().Offset.Value;
            state = other.LoadReceiverStateAsync($"{queue}\n{queue}\n0", TestCancellationToken).GetAwaiter().GetResult();
            return committed == hot.Offset && state is { Finished.Count: > 0 };
        }, TimeSpan.FromSeconds(30));

        Assert.Equal(hot.Offset, state!.AnchorOffset);
        Assert.Equal(hot.MessageId, state.AnchorMessageId);
        Assert.Equal([(hot.Offset + 30, hot.Offset + 1130)], state.Finished);
        Assert.All(Enumerable.Range(1, 29), i => Assert.False(state.IsFinished(hot.Offset + i)));
    }

    [Theory]
    [InlineData(10_000, 40)]
    [InlineData(20, 100_000)]
    public async Task Receive_PastTheSkippedLimits_PausesThePartitionAndWarnsWithTheGroupKey(int skippedLimit, long spanLimit)
    {
        Log.MaxLogEntriesToStore = 10_000;
        var transport = Track(new KafkaMessageTransport(CreateOptions() with
        {
            Partitions = 1,
            DelayedDelivery = false,
            ReceiverBufferLimit = 8,
            ReceiverGroupHeldLimit = 5,
            ReceiverSkippedLimit = skippedLimit,
            ReceiverSkippedSpanLimit = spanLimit
        }));
        var source = MessageSource.Queue(NewName("skip-limit"));
        var destination = MessageDestination.Queue(source.Name);
        await using var receiver = (KafkaMessageReceiver)await transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken);
        await transport.SendAsync(destination, Enumerable.Range(0, 200).Select(i => Message($"busy-{i}", $"busy-{i}") with { GroupKey = "busy-group" }).ToList(), TestCancellationToken);
        await transport.SendAsync(destination, Enumerable.Range(0, 50).Select(i => Message($"cold-{i}", $"cold-{i}") with { GroupKey = $"cold-{i}" }).ToList(), TestCancellationToken);

        // Skipping the busy group reaches a limit (its records are contiguous, so as many are skipped as offsets spanned),
        // and the receiver falls back to stopping the reading, then pausing the partition, and says which group did it.
        var first = await ReceiveOneAsync(receiver);
        await WaitForAsync(() => receiver.PausedCount == 1);
        Assert.Equal((int)Math.Min(skippedLimit, spanLimit), receiver.SkippedCount);
        Assert.Equal(1, receiver.HeldStops);
        Assert.Equal(1, receiver.Pauses);
        Assert.Single(Log.LogEntries, e => e.LogLevel == LogLevel.Warning && e.Message.Contains("busy-group", StringComparison.Ordinal));
        await AssertNothingReceivedAsync(receiver, TimeSpan.FromSeconds(1));

        // Once the group is handled, the partition is read again, and nothing is lost or reordered.
        await receiver.CompleteAsync(first, TestCancellationToken);
        var received = new List<string> { first.MessageId };
        received.AddRange(await DrainAsync(receiver, 249, TimeSpan.FromSeconds(60)));
        Assert.Equal(Enumerable.Range(0, 200).Select(i => $"busy-{i}"), received.Where(id => id.StartsWith("busy-", StringComparison.Ordinal)));
        Assert.Equal(Enumerable.Range(0, 50).Select(i => $"cold-{i}").Order(), received.Where(id => id.StartsWith("cold-", StringComparison.Ordinal)).Order());
        await WaitForAsync(() => receiver.PausedCount == 0);
        Assert.Equal(receiver.SkippedTotal, receiver.ReadAgainCount);
    }

    [Fact]
    public async Task Receive_WithSkippedRecordsDeletedFromKafka_DropsThemAndDeliversTheRest()
    {
        Log.MaxLogEntriesToStore = 10_000;
        var transport = Track(new KafkaMessageTransport(CreateOptions() with { Partitions = 1, DelayedDelivery = false, ReceiverBufferLimit = 8, ReceiverGroupHeldLimit = 10 }));
        var source = MessageSource.Queue(NewName("skip-deleted"));
        var destination = MessageDestination.Queue(source.Name);
        await using var receiver = (KafkaMessageReceiver)await transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken);
        await transport.SendAsync(destination, Enumerable.Range(0, 60).Select(i => Message($"hot-{i}", $"hot-{i}") with { GroupKey = "hot" }).ToList(), TestCancellationToken);
        await transport.SendAsync(destination, Enumerable.Range(0, 10).Select(i => Message($"cold-{i}", $"cold-{i}") with { GroupKey = $"cold-{i}" }).ToList(), TestCancellationToken);

        var first = (KafkaIncomingMessage)await ReceiveOneAsync(receiver);
        Assert.Equal(10, (await DrainAsync(receiver, 10)).Count);
        int lowest = (int)(receiver.LowestSkippedOffset(first.Partition.TopicPartition) - first.Offset);
        Assert.InRange(lowest, 10, 11);

        // Retention deletes the oldest records, and with them skipped records of the hot group, before they are read again.
        using (var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = KafkaTestConnection.BootstrapServers }).Build())
            await admin.DeleteRecordsAsync([new TopicPartitionOffset(first.Partition.TopicPartition, first.Offset + 40)]);

        // The reader finds them gone, so the receiver drops them instead of waiting for them, and delivers the rest in order.
        await receiver.CompleteAsync(first, TestCancellationToken);
        var expected = Enumerable.Range(1, lowest - 1).Concat(Enumerable.Range(40, 20)).Select(i => $"hot-{i}").ToList();
        Assert.Equal(expected, await DrainAsync(receiver, expected.Count));
        await AssertNothingReceivedAsync(receiver, TimeSpan.FromSeconds(1));
        Assert.Equal(0, receiver.SkippedCount);
        Assert.Single(Log.LogEntries, e => e.LogLevel == LogLevel.Warning && e.Message.Contains("no longer in Kafka", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Send_GroupedMessagesToATopicWithOnePartition_WarnsOnce()
    {
        Log.MaxLogEntriesToStore = 10_000;
        var single = Track(new KafkaMessageTransport(CreateOptions() with { Partitions = 1, DelayedDelivery = false }));
        string grouped = NewName("one-grouped");
        string ungrouped = NewName("one-ungrouped");
        await single.SendAsync(MessageDestination.Queue(grouped), [Message("a") with { GroupKey = "customer-1" }], TestCancellationToken);
        await single.SendAsync(MessageDestination.Queue(grouped), [Message("b") with { GroupKey = "customer-2" }], TestCancellationToken);
        await single.SendAsync(MessageDestination.Queue(ungrouped), [Message("c")], TestCancellationToken);

        var several = Track(new KafkaMessageTransport(CreateOptions() with { Partitions = 2, DelayedDelivery = false }));
        string spread = NewName("two-grouped");
        await several.SendAsync(MessageDestination.Queue(spread), [Message("d") with { GroupKey = "customer-1" }], TestCancellationToken);

        var warning = Assert.Single(Log.LogEntries, e => e.LogLevel == LogLevel.Warning && e.Message.Contains("one partition", StringComparison.Ordinal));
        Assert.Contains(single.Names.Queue(grouped), warning.Message, StringComparison.Ordinal);
        Assert.Contains("busy group", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildState_WithTheOldestUnfinishedRecordSkipped_AnchorsOnItAndKeepsItsAttempt()
    {
        var state = new KafkaPartitionState(new TopicPartition("topic", 0));
        for (long offset = 0; offset < 10; offset++)
            state.Read(offset);

        // Offsets 5 and 9 are skipped records of one group; the rest are finished.
        state.Pending.ExceptWith([0, 1, 2, 3, 4, 6, 7, 8]);
        state.Skipped.Add("group", 5, "m5", null);
        state.Skipped.Add("group", 9, "m9", new KafkaReceiverAttempt("m9", 3, null));

        var snapshot = KafkaMessageReceiver.BuildState(state, [], []);
        Assert.Equal(5, snapshot.AnchorOffset);
        Assert.Equal("m5", snapshot.AnchorMessageId);
        Assert.Equal([(6L, 9L)], snapshot.Finished);
        Assert.Equal(3, snapshot.Attempts[9].DeliveryCount);
    }

    [Fact]
    public void SkippedRecords_RemovingTheFirstOfEachGroup_KeepsGroupsInOffsetOrderAndTracksTheLowest()
    {
        var skipped = new KafkaSkippedRecords();
        skipped.Add("a", 10, "a10", null);
        skipped.Add("b", 12, "b12", null);
        skipped.Add("a", 15, "a15", null);
        Assert.Equal(3, skipped.Count);
        Assert.Equal(10, skipped.LowestOffset);
        Assert.Equal([10L, 12L], skipped.FirstOffsets.Select(f => f.Key));
        Assert.True(skipped.TryGetGroupAt(12, out string key));
        Assert.Equal("b", key);
        Assert.False(skipped.TryGetGroupAt(15, out _));

        skipped.RemoveFirst("a");
        Assert.Equal(12, skipped.LowestOffset);
        Assert.Equal([12L, 15L], skipped.FirstOffsets.Select(f => f.Key));
        Assert.True(skipped.TryGetMessageIdAt(15, out string messageId));
        Assert.Equal("a15", messageId);

        skipped.RemoveFirst("b");
        skipped.RemoveFirst("a");
        Assert.Equal(0, skipped.Count);
        Assert.Equal(-1, skipped.LowestOffset);
        Assert.False(skipped.Contains("a"));
    }

    [Fact]
    public async Task Receive_WithGroupBacklogsOverTheirPartitionsHeldLimit_PausesOnlyThatPartition()
    {
        var transport = Track(new KafkaMessageTransport(CreateOptions() with
        {
            Partitions = 2,
            DelayedDelivery = false,
            ReceiverBufferLimit = 8,
            ReceiverHeldLimit = 20
        }));
        var source = MessageSource.Queue(NewName("hot-partition"));
        var destination = MessageDestination.Queue(source.Name);
        await using var receiver = (KafkaMessageReceiver)await transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken);

        // Learn which partition each group key lands on.
        await transport.SendAsync(destination, Enumerable.Range(0, 40).Select(i => Message("probe", $"probe-{i}") with { GroupKey = $"key-{i}" }).ToList(), TestCancellationToken);
        var probes = await ReceiveAsync(receiver, 40);
        foreach (var probe in probes)
            await receiver.CompleteAsync(probe, TestCancellationToken);

        var partitions = probes.ToDictionary(m => m.GroupKey!, m => ((KafkaIncomingMessage)m).Partition.TopicPartition.Partition.Value);
        int hotPartition = partitions["key-0"];
        var hotKeys = partitions.Where(p => p.Value == hotPartition).Select(p => p.Key).Take(4).ToList();
        var coldKeys = partitions.Where(p => p.Value != hotPartition).Select(p => p.Key).ToList();
        Assert.Equal(4, hotKeys.Count);
        Assert.NotEmpty(coldKeys);

        // Several groups each hold fewer records than their own budget, so none is skipped, but together they reach
        // their partition's limit, which pauses it.
        await transport.SendAsync(destination, Enumerable.Range(0, 15).SelectMany(i => hotKeys.Select(k => Message($"{k}-hot-{i}", $"{k}-hot-{i}") with { GroupKey = k })).ToList(), TestCancellationToken);
        var heads = new List<IncomingMessage>();
        foreach (string _ in hotKeys)
            heads.Add(await ReceiveOneAsync(receiver));

        Assert.Equal(hotKeys.Select(k => $"{k}-hot-0"), heads.Select(m => m.MessageId));
        await WaitForAsync(() => receiver.PausedCount > 0);

        // While the hot groups' first messages are still being handled, the other partition keeps being read.
        var cold = coldKeys.SelectMany(k => Enumerable.Range(0, 3).Select(i => $"{k}-{i}")).ToList();
        await transport.SendAsync(destination, cold.Select(id => Message(id, id) with { GroupKey = id[..id.LastIndexOf('-')] }).ToList(), TestCancellationToken);
        var coldReceived = await DrainAsync(receiver, cold.Count);
        Assert.Equal(cold.Order(), coldReceived.Order());
        foreach (string key in coldKeys)
            Assert.Equal(Enumerable.Range(0, 3).Select(i => $"{key}-{i}"), coldReceived.Where(id => id.StartsWith($"{key}-", StringComparison.Ordinal)));

        Assert.Equal(1, receiver.PausedCount);
        Assert.Equal(0, receiver.SkippedTotal);

        // Once the hot groups are handled, their partition is read again, and nothing of their backlogs is lost or reordered.
        foreach (var head in heads)
            await receiver.CompleteAsync(head, TestCancellationToken);

        var received = heads.Select(m => m.MessageId).ToList();
        received.AddRange(await DrainAsync(receiver, 15 * hotKeys.Count - heads.Count, TimeSpan.FromSeconds(60)));
        foreach (string key in hotKeys)
            Assert.Equal(Enumerable.Range(0, 15).Select(i => $"{key}-hot-{i}"), received.Where(id => id.StartsWith($"{key}-hot-", StringComparison.Ordinal)));

        await WaitForAsync(() => receiver.PausedCount == 0);
    }

    [Fact]
    public void CreateConsumerConfig_ByDefault_FetchesSoonAfterAFullQueueIsRead()
    {
        var transport = Track(new KafkaMessageTransport(CreateOptions()));
        Assert.Equal(100, transport.CreateConsumerConfig("group").FetchQueueBackoffMs);

        var tuned = Track(new KafkaMessageTransport(CreateOptions() with { ConfigureConsumer = c => c.FetchQueueBackoffMs = 500 }));
        Assert.Equal(500, tuned.CreateConsumerConfig("group").FetchQueueBackoffMs);
    }

    [Fact]
    public async Task Receive_WithAGroupBacklogThatDrainsQuickly_StopsReadingWithoutPausingItsPartition()
    {
        var transport = Track(new KafkaMessageTransport(CreateOptions() with
        {
            Partitions = 1,
            DelayedDelivery = false,
            ReceiverBufferLimit = 8,
            ReceiverHeldLimit = 20,
            ReceiverHeldStopTimeout = TimeSpan.FromSeconds(30)
        }));
        var source = MessageSource.Queue(NewName("hot-drain"));
        await transport.SendAsync(MessageDestination.Queue(source.Name), Enumerable.Range(0, 300).Select(i => Message($"hot-{i}", $"hot-{i}") with { GroupKey = "hot" }).ToList(), TestCancellationToken);
        await using var receiver = (KafkaMessageReceiver)await transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken);

        // While a batch of the group is unsettled, its next records are held until they reach the partition's limit.
        // Each batch is settled well within the stop timeout, so reaching the limit only stops the reading, which reads
        // on from what librdkafka fetched, and never pauses the partition.
        var order = new List<string>();
        for (int reached = 1; reached <= 5; reached++)
        {
            var batch = await receiver.ReceiveAsync(15, TimeSpan.FromSeconds(5), TestCancellationToken);
            await WaitForAsync(() => receiver.HeldStops + receiver.Pauses >= reached);
            foreach (var message in batch)
            {
                order.Add(message.MessageId);
                await receiver.CompleteAsync(message, TestCancellationToken);
            }
        }

        order.AddRange(await DrainAsync(receiver, 300 - order.Count, TimeSpan.FromSeconds(60)));
        Assert.Equal(Enumerable.Range(0, 300).Select(i => $"hot-{i}"), order);
        Assert.Equal(0, receiver.Pauses);
    }

    [Fact]
    public async Task Receive_WithGroupBacklogsThatDidNotDrainInTime_PausesTheirPartitionAtOnceTheNextTime()
    {
        var stopTimeout = TimeSpan.FromMilliseconds(100);
        var transport = Track(new KafkaMessageTransport(CreateOptions() with
        {
            Partitions = 1,
            DelayedDelivery = false,
            ReceiverBufferLimit = 8,
            ReceiverHeldLimit = 20,
            ReceiverHeldStopTimeout = stopTimeout
        }));
        var source = MessageSource.Queue(NewName("hot-slow"));
        string[] keys = ["a", "b", "c", "d"];
        await transport.SendAsync(MessageDestination.Queue(source.Name), Enumerable.Range(0, 25).SelectMany(i => keys.Select(k => Message($"{k}-{i}", $"{k}-{i}") with { GroupKey = k })).ToList(), TestCancellationToken);
        await using var receiver = (KafkaMessageReceiver)await transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken);

        // The groups' first messages are not settled, so their backlogs, each well under a group's own budget, together
        // reach the partition's limit: that stops the reading, then pauses the partition.
        var heads = new List<IncomingMessage>();
        foreach (string _ in keys)
            heads.Add(await ReceiveOneAsync(receiver));

        Assert.Equal(keys.Select(k => $"{k}-0"), heads.Select(m => m.MessageId));
        await WaitForAsync(() => receiver.PausedCount == 1);
        Assert.Equal(1, receiver.HeldStops);
        Assert.Equal(1, receiver.Pauses);
        Assert.Equal(20, receiver.HeldCount);

        // It stays paused for longer than the stop timeout before it drains, which handing out the next 15 does, so the
        // next time the backlogs reach the limit, while those 15 are unsettled, the partition is paused at once.
        await Task.Delay(stopTimeout * 3, TestCancellationToken);
        foreach (var head in heads)
            await receiver.CompleteAsync(head, TestCancellationToken);

        var batch = await receiver.ReceiveAsync(15, TimeSpan.FromSeconds(5), TestCancellationToken);
        Assert.Equal(keys.Take(3).SelectMany(k => Enumerable.Range(1, 5).Select(i => $"{k}-{i}")), batch.Select(m => m.MessageId));
        await WaitForAsync(() => receiver.Pauses == 2);
        Assert.Equal(1, receiver.HeldStops);
        Assert.Equal(1, receiver.PausedCount);
        Assert.Equal(0, receiver.SkippedTotal);

        foreach (var message in batch)
            await receiver.CompleteAsync(message, TestCancellationToken);

        var received = heads.Concat(batch).Select(m => m.MessageId).ToList();
        received.AddRange(await DrainAsync(receiver, 100 - received.Count, TimeSpan.FromSeconds(60)));
        foreach (string key in keys)
            Assert.Equal(Enumerable.Range(0, 25).Select(i => $"{key}-{i}"), received.Where(id => id.StartsWith($"{key}-", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Rebalance_OfAPartitionPausedForAGroupBacklogAndBack_ResumesReadingIt()
    {
        var transport = Track(new KafkaMessageTransport(CreateOptions() with
        {
            Partitions = 2,
            DelayedDelivery = false,
            RebalanceGracePeriod = TimeSpan.FromMilliseconds(500),
            ReceiverBufferLimit = 8,
            ReceiverHeldLimit = 10
        }));
        var source = MessageSource.Queue(NewName("held-moved"));
        var destination = MessageDestination.Queue(source.Name);
        await using var first = (KafkaMessageReceiver)await transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken);

        // A group with a backlog on each partition, so whichever partition moves is paused for it.
        await transport.SendAsync(destination, Enumerable.Range(0, 20).Select(i => Message("probe", $"probe-{i}") with { GroupKey = $"key-{i}" }).ToList(), TestCancellationToken);
        var probes = await ReceiveAsync(first, 20);
        foreach (var probe in probes)
            await first.CompleteAsync(probe, TestCancellationToken);

        string[] keys = [.. probes.GroupBy(m => ((KafkaIncomingMessage)m).Partition.TopicPartition.Partition.Value).Select(g => g.First().GroupKey!)];
        Assert.Equal(2, keys.Length);
        await transport.SendAsync(destination, keys.SelectMany(k => Enumerable.Range(0, 40).Select(i => Message($"{k}-{i}", $"{k}-{i}") with { GroupKey = k })).ToList(), TestCancellationToken);
        // One at a time: once a group's first message is handed out, the next one received is the other group's.
        IncomingMessage[] handling = [await ReceiveOneAsync(first), await ReceiveOneAsync(first)];
        Assert.Equal(keys.Order(), handling.Select(m => m.GroupKey!).Order());
        await WaitForAsync(() => first.PausedCount == 2);

        await using (var second = (KafkaMessageReceiver)await transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken))
            await WaitForAsync(() => first.Assignment.Count == 1 && second.Assignment.Count == 1, TimeSpan.FromSeconds(30));

        await WaitForAsync(() => first.Assignment.Count == 2, TimeSpan.FromSeconds(30));

        // The moved partition's message is delivered again; the other one is still this receiver's to complete.
        var moved = new List<string>();
        foreach (var message in handling)
        {
            try
            {
                await first.CompleteAsync(message, TestCancellationToken);
            }
            catch (MessageLeaseLostException)
            {
                moved.Add(message.GroupKey!);
            }
        }

        Assert.Single(moved);
        var received = await DrainAsync(first, 79, TimeSpan.FromSeconds(60));
        foreach (string key in keys)
        {
            int start = moved.Contains(key) ? 0 : 1;
            Assert.Equal(Enumerable.Range(start, 40 - start).Select(i => $"{key}-{i}"), received.Where(id => id.StartsWith($"{key}-", StringComparison.Ordinal)));
        }

        await WaitForAsync(() => first.PausedCount == 0);
    }

    [Fact]
    public async Task DeliverAt_ManyGroupedDelays_HoldsOnlyABoundedNumberInMemory()
    {
        var transport = Track(new KafkaMessageTransport(CreateOptions() with { Prefix = UniquePrefix(), Partitions = 1, ReceiverTimedLimit = 20 }));
        var source = MessageSource.Queue(NewName("many-delays"));
        await using var receiver = (KafkaMessageReceiver)await transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken);
        var due = DateTimeOffset.UtcNow.AddSeconds(8);
        await transport.SendAsync(MessageDestination.Queue(source.Name), Enumerable.Range(0, 50).Select(i => Message($"later-{i}", $"later-{i}") with { GroupKey = $"user-{i}", DeliverAt = due }).ToList(), TestCancellationToken);
        await transport.SendAsync(MessageDestination.Queue(source.Name), Enumerable.Range(0, 5).Select(i => Message($"now-{i}", $"now-{i}")).ToList(), TestCancellationToken);

        int mostHeld = 0;
        while (DateTimeOffset.UtcNow < due.AddSeconds(-2))
        {
            mostHeld = Math.Max(mostHeld, receiver.TimedCount);
            await Task.Delay(50, TestCancellationToken);
        }

        Assert.InRange(mostHeld, 1, 21);
        var messages = await ReceiveAsync(receiver, 55, TimeSpan.FromSeconds(30));
        Assert.Equal(55, messages.Select(m => m.MessageId).Distinct().Count());
        Assert.All(messages.Where(m => m.MessageId.StartsWith("later-", StringComparison.Ordinal)), _ => Assert.True(DateTimeOffset.UtcNow >= due));
    }

    [Fact]
    public async Task Rebalance_OfAPartitionWithRetriedGroupedMessages_KeepsTheirDeliveryCounts()
    {
        var transport = Track(new KafkaMessageTransport(CreateOptions() with { Partitions = 2, RebalanceGracePeriod = TimeSpan.FromMilliseconds(500) }));
        var source = MessageSource.Queue(NewName("grouped-rebalance"));
        await using var first = (KafkaMessageReceiver)await transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken);

        // Ten groups land on both partitions, so whichever one moves takes retried messages with it.
        await transport.SendAsync(MessageDestination.Queue(source.Name), Enumerable.Range(0, 10).Select(i => Message($"m{i}", $"m{i}") with { GroupKey = $"group-{i}" }).ToList(), TestCancellationToken);
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            var delivered = await ReceiveAsync(first, 10);
            Assert.All(delivered, m => Assert.Equal(attempt, m.DeliveryCount));
            foreach (var message in delivered)
                await first.AbandonAsync(message, TimeSpan.Zero, TestCancellationToken);
        }

        await using var second = (KafkaMessageReceiver)await transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken);
        await WaitForAsync(() => first.Assignment.Count == 1 && second.Assignment.Count == 1, TimeSpan.FromSeconds(30));

        var moved = new List<IncomingMessage>();
        var stayed = new List<IncomingMessage>();
        await WaitForAsync(() =>
        {
            moved.AddRange(second.ReceiveAsync(10, TimeSpan.FromMilliseconds(100), TestCancellationToken).GetAwaiter().GetResult());
            stayed.AddRange(first.ReceiveAsync(10, TimeSpan.FromMilliseconds(100), TestCancellationToken).GetAwaiter().GetResult());
            return moved.Count + stayed.Count >= 10;
        });

        Assert.NotEmpty(moved);
        Assert.Equal(10, moved.Concat(stayed).Select(m => m.MessageId).Distinct().Count());
        Assert.All(moved.Concat(stayed), m => Assert.Equal(3, m.DeliveryCount));
    }

    [Fact]
    public async Task Abandon_OfAGroupedMessage_KeepsItsDeliveryCountWhileTheReceiverRuns()
    {
        var options = CreateOptions() with { Partitions = 1 };
        var transport = Track(new KafkaMessageTransport(options));
        var source = MessageSource.Queue(NewName("grouped-crash"));
        await using var receiver = await transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken);
        await transport.SendAsync(MessageDestination.Queue(source.Name), [Message("poison", "poison") with { GroupKey = "customer-1" }], TestCancellationToken);
        var retryAt = DateTimeOffset.UtcNow.AddMinutes(1);
        await receiver.AbandonAsync(await ReceiveOneAsync(receiver), TimeSpan.FromMinutes(1), TestCancellationToken);

        // A process that stops without releasing its partitions leaves what it last wrote; another one reads it.
        await using var other = new KafkaMessageTransport(options);
        string queue = $"{options.Prefix}.queue.{source.Name}";
        KafkaReceiverAttempt? attempt = null;
        await WaitForAsync(() =>
        {
            var state = other.LoadReceiverStateAsync($"{queue}\n{queue}\n0", TestCancellationToken).GetAwaiter().GetResult();
            attempt = state?.Attempts.Values.SingleOrDefault();
            return attempt is not null;
        });

        Assert.Equal("poison", attempt!.MessageId);
        Assert.Equal(2, attempt.DeliveryCount);
        Assert.InRange(attempt.AvailableAt!.Value, retryAt.AddSeconds(-5), retryAt.AddSeconds(5));
    }

    [Fact]
    public async Task Dispose_WithARetriedGroupedMessage_KeepsItsDeliveryCount()
    {
        var transport = Track(new KafkaMessageTransport(CreateOptions() with { Partitions = 1 }));
        var source = MessageSource.Queue(NewName("grouped-count"));
        var first = await transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken);
        await transport.SendAsync(MessageDestination.Queue(source.Name), [Message("poison", "poison") with { GroupKey = "customer-1" }], TestCancellationToken);

        var message = await ReceiveOneAsync(first);
        for (int attempt = 1; attempt < 4; attempt++)
        {
            Assert.Equal(attempt, message.DeliveryCount);
            await first.AbandonAsync(message, TimeSpan.Zero, TestCancellationToken);
            message = await ReceiveOneAsync(first);
        }

        await first.DisposeAsync();

        await using var second = await transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken);
        var again = await ReceiveOneAsync(second);
        Assert.Equal("poison", again.MessageId);
        Assert.Equal(5, again.DeliveryCount);
    }

    [Fact]
    public async Task Dispose_WithAGroupedMessageWaitingToBeRetried_KeepsItsRetryDelay()
    {
        var transport = Track(new KafkaMessageTransport(CreateOptions() with { Partitions = 1 }));
        var source = MessageSource.Queue(NewName("grouped-delay"));
        var first = await transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken);
        await transport.SendAsync(MessageDestination.Queue(source.Name), [Message("retry", "retry") with { GroupKey = "customer-1" }], TestCancellationToken);
        var message = await ReceiveOneAsync(first);
        var retryAt = DateTimeOffset.UtcNow.AddSeconds(6);
        await first.AbandonAsync(message, TimeSpan.FromSeconds(6), TestCancellationToken);
        await first.DisposeAsync();

        await using var second = await transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken);
        var again = await ReceiveOneAsync(second, TimeSpan.FromSeconds(30));
        Assert.True(DateTimeOffset.UtcNow >= retryAt.AddSeconds(-1), $"The retry was delivered {retryAt - DateTimeOffset.UtcNow} early.");
        Assert.Equal(2, again.DeliveryCount);
    }

    [Fact]
    public async Task Dispose_WithAnUnsettledGroupedMessage_RedeliversItWithAHigherDeliveryCount()
    {
        var transport = Track(new KafkaMessageTransport(CreateOptions() with { Partitions = 1 }));
        var source = MessageSource.Queue(NewName("grouped-dispose"));
        var first = await transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken);
        await transport.SendAsync(MessageDestination.Queue(source.Name), [Message("orphan", "orphan") with { GroupKey = "customer-1" }], TestCancellationToken);
        Assert.Equal(1, (await ReceiveOneAsync(first)).DeliveryCount);
        await first.DisposeAsync();

        await using var second = await transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken);
        Assert.Equal(2, (await ReceiveOneAsync(second)).DeliveryCount);
    }

    [Fact]
    public async Task Dispose_WithADelayedGroupedMessageHeld_DoesNotRedeliverWhatWasCompletedBehindIt()
    {
        var transport = Track(new KafkaMessageTransport(CreateOptions() with { Partitions = 1 }));
        var source = MessageSource.Queue(NewName("pinned"));
        var first = await transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken);
        await transport.SendAsync(MessageDestination.Queue(source.Name), [Message("reminder", "reminder") with { GroupKey = "user-1", DeliverAt = DateTimeOffset.UtcNow.AddMinutes(30) }], TestCancellationToken);
        await transport.SendAsync(MessageDestination.Queue(source.Name), Enumerable.Range(0, 10).Select(i => Message($"now-{i}", $"now-{i}")).ToList(), TestCancellationToken);
        foreach (var message in await ReceiveAsync(first, 10))
            await first.CompleteAsync(message, TestCancellationToken);

        await first.DisposeAsync();

        await using var second = await transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken);
        await AssertNothingReceivedAsync(second, TimeSpan.FromSeconds(3));
    }

    /// <summary>A prefix of its own, for tests that need the delay forwarders' consumer group to themselves.</summary>
    private static string UniquePrefix() => $"{Prefix}-{Guid.NewGuid():N}"[..(Prefix.Length + 9)];

    /// <summary>Receives and completes <paramref name="count"/> messages, returning their ids in the order they were received.</summary>
    private async Task<List<string>> DrainAsync(IMessageReceiver receiver, int count, TimeSpan? timeout = null)
    {
        var received = new List<string>();
        var deadline = DateTime.UtcNow + (timeout ?? DeliveryTimeout);
        while (received.Count < count)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException($"Received {received.Count} of {count} messages from {receiver.Source}: {String.Join(", ", received)}.");

            var batch = await receiver.ReceiveAsync(10, remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1), TestCancellationToken);
            foreach (var message in batch)
            {
                received.Add(message.MessageId);
                await receiver.CompleteAsync(message, TestCancellationToken);
            }
        }

        return received;
    }

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
