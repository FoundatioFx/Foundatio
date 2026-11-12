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
