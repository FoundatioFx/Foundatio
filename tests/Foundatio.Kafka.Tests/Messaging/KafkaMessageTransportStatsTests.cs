using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Confluent.Kafka;
using Foundatio.Messaging.Transport;
using Foundatio.Xunit;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Foundatio.Kafka.Tests.Messaging;

public class KafkaMessageTransportStatsTests(ITestOutputHelper output) : TestWithLoggingBase(output)
{
    private const string Prefix = "foundatio-tests";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly List<KafkaMessageTransport> _transports = [];
    private readonly List<MessageSource> _sources = [];

    [Fact]
    public async Task GetStatsAsync_UnknownSources_CountAsEmptyAndCreateNothing()
    {
        // Arrange
        var transport = CreateTransport();
        string name = NewName("stats-unknown");
        var queue = MessageSource.Queue(name);
        var subscription = MessageSource.Subscription(name, "nobody");

        // Act
        var queueStats = await transport.GetStatsAsync(queue, TestCancellationToken);
        var subscriptionStats = await transport.GetStatsAsync(subscription, TestCancellationToken);

        // Assert
        foreach (var (source, stats) in new[] { (queue, queueStats), (subscription, subscriptionStats) })
        {
            Assert.NotNull(stats);
            Assert.Equal(source, stats.Source);
            Assert.Equal(0, stats.Queued);
            Assert.Equal(0, stats.DeadLettered);
            Assert.Null(stats.InFlight);
            Assert.Null(stats.Delayed);
        }

        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = KafkaTestConnection.BootstrapServers }).Build();
        var topics = admin.GetMetadata(TimeSpan.FromSeconds(10)).Topics.Where(t => !t.Error.IsError).Select(t => t.Topic);
        var groups = (await admin.ListConsumerGroupsAsync()).Valid.Select(g => g.GroupId);
        Assert.DoesNotContain(topics, t => t.Contains(name, StringComparison.Ordinal));
        Assert.DoesNotContain(groups, g => g.Contains(name, StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetStatsAsync_ReceivedMessages_StayQueuedUntilTheirOffsetIsCommitted()
    {
        // Arrange
        var transport = CreateTransport(o => o with { Partitions = 1 });
        var source = Created(MessageSource.Queue(NewName("stats-queued")));
        await transport.SendAsync(MessageDestination.Queue(source.Name), [Message("a"), Message("b"), Message("c")], TestCancellationToken);

        // Act & Assert: sent before any receiver, so a new group starts at the earliest offset
        await WaitForStatsAsync(transport, source, s => s.Queued == 3);

        await using var receiver = await transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken);
        var messages = await ReceiveAsync(receiver, 3);
        var stats = await transport.GetStatsAsync(source, TestCancellationToken);
        Assert.Equal(3, stats!.Queued);
        Assert.Null(stats.InFlight);

        await receiver.CompleteAsync(messages[0], TestCancellationToken);
        await WaitForStatsAsync(transport, source, s => s.Queued == 2);

        await receiver.CompleteAsync(messages[2], TestCancellationToken);
        await receiver.CompleteAsync(messages[1], TestCancellationToken);
        await WaitForStatsAsync(transport, source, s => s.Queued == 0);
    }

    [Fact]
    public async Task GetStatsAsync_DeadLetteredMessages_CountUntilTheyAreCompletedFromTheDeadLetterQueue()
    {
        // Arrange
        var transport = CreateTransport(o => o with { Partitions = 1 });
        var source = Created(MessageSource.Queue(NewName("stats-dead")));
        await using var receiver = await transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken);
        await transport.SendAsync(MessageDestination.Queue(source.Name), [Message("a"), Message("b")], TestCancellationToken);

        // Act
        foreach (var message in await ReceiveAsync(receiver, 2))
            await receiver.DeadLetterAsync(message, "stats", TestCancellationToken);

        // Assert
        await WaitForStatsAsync(transport, source, s => s.Queued == 0 && s.DeadLettered == 2);

        // A peek releases dead letters in place; a replay completes them.
        await using (var deadLetters = await transport.CreateReceiverAsync(MessageSource.DeadLetter(source), new ReceiverOptions(), TestCancellationToken))
        {
            var peeked = await ReceiveAsync(deadLetters, 2);
            await deadLetters.CompleteAsync(peeked[0], TestCancellationToken);
            await deadLetters.AbandonAsync(peeked[1], TimeSpan.Zero, TestCancellationToken);
        }

        await WaitForStatsAsync(transport, source, s => s.DeadLettered == 1);
    }

    [Fact]
    public async Task GetStatsAsync_Subscriptions_EachCountTheirOwnCopies()
    {
        // Arrange
        var transport = CreateTransport();
        string topic = NewName("stats-topic");
        var first = Created(MessageSource.Subscription(topic, "first"));
        var second = Created(MessageSource.Subscription(topic, "second"));

        // Published before either subscription exists, so neither gets a copy; one sent to a subscription waits for it.
        await transport.SendAsync(MessageDestination.Topic(topic), [Message("before")], TestCancellationToken);
        await transport.SendAsync(MessageDestination.Subscription(topic, second.Name), [Message("direct")], TestCancellationToken);
        await WaitForStatsAsync(transport, second, s => s.Queued == 1);
        Assert.Equal(0, (await transport.GetStatsAsync(first, TestCancellationToken))!.Queued);

        await using var firstReceiver = await transport.CreateReceiverAsync(first, new ReceiverOptions(), TestCancellationToken);
        await using var secondReceiver = await transport.CreateReceiverAsync(second, new ReceiverOptions(), TestCancellationToken);

        // Act
        await transport.SendAsync(MessageDestination.Topic(topic), [Message("a"), Message("b")], TestCancellationToken);

        // Assert
        await WaitForStatsAsync(transport, first, s => s.Queued == 2);
        await WaitForStatsAsync(transport, second, s => s.Queued == 3);

        foreach (var message in await ReceiveAsync(firstReceiver, 2))
            await firstReceiver.CompleteAsync(message, TestCancellationToken);

        await WaitForStatsAsync(transport, first, s => s.Queued == 0);
        Assert.Equal(3, (await transport.GetStatsAsync(second, TestCancellationToken))!.Queued);
    }

    [Fact]
    public Task GetStatsAsync_DeadLetterSource_Throws()
    {
        var transport = CreateTransport();
        var source = MessageSource.DeadLetter(MessageSource.Queue(NewName("stats-dlq")));

        return Assert.ThrowsAsync<ArgumentException>(() => transport.GetStatsAsync(source, TestCancellationToken));
    }

    [Fact]
    public async Task GetStatsAsync_InstanceSubscription_ReturnsNull()
    {
        var transport = CreateTransport();
        await using var receiver = await transport.CreateReceiverAsync(MessageSource.InstanceSubscription(NewName("stats-instance")), new ReceiverOptions(), TestCancellationToken);

        Assert.Null(await transport.GetStatsAsync(receiver.Source, TestCancellationToken));
    }

    [Fact]
    public async Task GetStatsAsync_CalledRepeatedlyWithAnOpenReceiver_NeverJoinsItsGroup()
    {
        // Arrange
        var transport = CreateTransport();
        var source = Created(MessageSource.Queue(NewName("stats-group")));
        await using var receiver = (KafkaMessageReceiver)await transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken);
        await WaitForAsync(() => receiver.Assignment.Count == 3);
        await transport.SendAsync(MessageDestination.Queue(source.Name), [Message("a")], TestCancellationToken);

        // Act
        var stopwatch = Stopwatch.StartNew();
        await transport.GetStatsAsync(source, TestCancellationToken);
        var first = stopwatch.Elapsed;

        const int calls = 20;
        stopwatch.Restart();
        for (int i = 0; i < calls; i++)
            Assert.Equal(1, (await transport.GetStatsAsync(source, TestCancellationToken))!.Queued);

        var average = stopwatch.Elapsed / calls;
        _logger.LogInformation("First stats call took {First}; the next {Calls} took {Average} each", first, calls, average);

        // Assert
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = KafkaTestConnection.BootstrapServers }).Build();
        var group = (await admin.DescribeConsumerGroupsAsync([$"{Prefix}.queue.{source.Name}"])).ConsumerGroupDescriptions.Single();
        Assert.Single(group.Members);
        Assert.Equal(3, receiver.Assignment.Count);
        Assert.True(average < TimeSpan.FromSeconds(1), $"A stats call took {average} on average.");
    }

    [Fact]
    public async Task GetStatsAsync_OfADeletedSource_CountsAsEmptyWithoutHoldingUpTheDelete()
    {
        // Arrange
        var transport = CreateTransport();
        var source = MessageSource.Queue(NewName("stats-deleted"));
        await using (var receiver = await transport.CreateReceiverAsync(source, new ReceiverOptions(), TestCancellationToken))
        {
            await transport.SendAsync(MessageDestination.Queue(source.Name), [Message("a"), Message("b")], TestCancellationToken);
            await receiver.DeadLetterAsync((await ReceiveAsync(receiver, 1))[0], "stats", TestCancellationToken);
        }

        await WaitForStatsAsync(transport, source, s => s.Queued == 1 && s.DeadLettered == 1);
        Assert.InRange(transport.StatsReaderCount, 1, 2);

        // Act
        await transport.DeleteAsync(source, TestCancellationToken);

        // Assert
        var stats = await transport.GetStatsAsync(source, TestCancellationToken);
        Assert.Equal(0, stats!.Queued);
        Assert.Equal(0, stats.DeadLettered);
    }

    [Fact]
    public async Task GetStatsAsync_IdleOffsetReaders_AreDisposed()
    {
        // Arrange
        var transport = CreateTransport(o => o with { StatsReaderIdleTimeout = TimeSpan.FromMilliseconds(200) });
        var source = Created(MessageSource.Queue(NewName("stats-idle")));
        await transport.SendAsync(MessageDestination.Queue(source.Name), [Message("a")], TestCancellationToken);
        await WaitForStatsAsync(transport, source, s => s.Queued == 1);
        Assert.InRange(transport.StatsReaderCount, 1, 2);

        // Act & Assert
        await WaitForAsync(() => transport.StatsReaderCount == 0);
        Assert.Equal(1, (await transport.GetStatsAsync(source, TestCancellationToken))!.Queued);
    }

    [Fact]
    public async Task GetStatsAsync_PastTheReaderLimit_ReadsWithShortLivedConsumers()
    {
        // Arrange
        var transport = CreateTransport(o => o with { MaxStatsReaders = 1 });
        var first = Created(MessageSource.Queue(NewName("stats-limit")));
        var second = Created(MessageSource.Queue(NewName("stats-limit")));
        await transport.SendAsync(MessageDestination.Queue(first.Name), [Message("a")], TestCancellationToken);
        await transport.SendAsync(MessageDestination.Queue(second.Name), [Message("a"), Message("b")], TestCancellationToken);

        // Act & Assert
        await WaitForStatsAsync(transport, first, s => s.Queued == 1);
        await WaitForStatsAsync(transport, second, s => s.Queued == 2);
        Assert.Equal(1, transport.StatsReaderCount);
    }

    [Fact]
    public async Task GetStatsAsync_AfterDispose_ThrowsAndLeavesNoReaders()
    {
        // Arrange
        var transport = new KafkaMessageTransport(CreateOptions());
        var source = MessageSource.Queue(NewName("stats-dispose"));
        await transport.GetStatsAsync(source, TestCancellationToken);

        // Act
        await transport.DisposeAsync();

        // Assert
        Assert.Equal(0, transport.StatsReaderCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => transport.GetStatsAsync(source, TestCancellationToken));
    }

    public override async ValueTask DisposeAsync()
    {
        if (_transports.Count > 0)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await Task.WhenAll(_sources.Select(async source =>
            {
                try
                {
                    await _transports[0].DeleteAsync(source, timeout.Token);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not delete {Source} after the test", source);
                }
            }));
        }

        foreach (var transport in _transports)
            await transport.DisposeAsync();

        await base.DisposeAsync();
    }

    private KafkaMessageTransport CreateTransport(Func<KafkaMessageTransportOptions, KafkaMessageTransportOptions>? configure = null)
    {
        var options = CreateOptions();
        var transport = new KafkaMessageTransport(configure?.Invoke(options) ?? options);
        _transports.Add(transport);
        return transport;
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

    private MessageSource Created(MessageSource source)
    {
        _sources.Add(source);
        return source;
    }

    private async Task<MessageSourceStats> WaitForStatsAsync(IMessageTransport transport, MessageSource source, Func<MessageSourceStats, bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            var stats = await transport.GetStatsAsync(source, TestCancellationToken);
            Assert.NotNull(stats);
            if (condition(stats))
                return stats;

            if (DateTime.UtcNow >= deadline)
                Assert.Fail($"The stats of {source} did not match within {Timeout}; the last were {stats}.");

            await Task.Delay(100, TestCancellationToken);
        }
    }

    private async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                Assert.Fail($"The condition did not hold within {Timeout}.");

            await Task.Delay(100, TestCancellationToken);
        }
    }

    private async Task<IReadOnlyList<IncomingMessage>> ReceiveAsync(IMessageReceiver receiver, int count)
    {
        var messages = new List<IncomingMessage>();
        var deadline = DateTime.UtcNow + Timeout;
        while (messages.Count < count)
        {
            if (DateTime.UtcNow >= deadline)
                Assert.Fail($"Received {messages.Count} of {count} messages from {receiver.Source} within {Timeout}.");

            messages.AddRange(await receiver.ReceiveAsync(count - messages.Count, TimeSpan.FromSeconds(1), TestCancellationToken));
        }

        return messages;
    }

    private static string NewName(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..(prefix.Length + 13)];

    private static OutgoingMessage Message(string body) => new() { MessageId = Guid.NewGuid().ToString("N"), Body = Encoding.UTF8.GetBytes(body) };
}
