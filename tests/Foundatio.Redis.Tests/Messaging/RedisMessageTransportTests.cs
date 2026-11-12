using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Foundatio.Messaging.Transport;
using Foundatio.Tests.Messaging;
using StackExchange.Redis;
using Xunit;

namespace Foundatio.Redis.Tests.Messaging;

public class RedisMessageTransportTests(ITestOutputHelper output) : MessageTransportTestsBase(output)
{
    private const string KeyPrefix = "foundatio-tests:messaging";

    protected override IMessageTransport CreateTransport()
    {
        return new RedisMessageTransport(GetMuxer(), new RedisMessageTransportOptions { KeyPrefix = KeyPrefix, LoggerFactory = Log });
    }

    [Fact]
    public async Task InstanceSubscription_OfAStoppedProcess_IsRemovedByTheNextPublish()
    {
        string topic = NewName("crash");
        var options = new RedisMessageTransportOptions { KeyPrefix = KeyPrefix, LoggerFactory = Log, InstanceSubscriptionTimeout = TimeSpan.FromSeconds(1) };
        var crashedConnection = await ConnectionMultiplexer.ConnectAsync(GetMuxer().Configuration);
        var crashed = new RedisMessageTransport(crashedConnection, options);
        await crashed.CreateReceiverAsync(MessageSource.InstanceSubscription(topic, "crashed"), new ReceiverOptions(), TestCancellationToken);
        Assert.NotEmpty(await FindKeysAsync($"*{{i:{topic}/crashed-*"));

        await crashedConnection.CloseAsync(allowCommandsToComplete: false);
        await Task.Delay(TimeSpan.FromSeconds(1.5), TestCancellationToken);

        var survivor = Track(new RedisMessageTransport(GetMuxer(), options));
        await using var receiver = await survivor.CreateReceiverAsync(MessageSource.InstanceSubscription(topic, "survivor"), new ReceiverOptions(), TestCancellationToken);
        await survivor.SendAsync(MessageDestination.Topic(topic), [Message("after crash")], TestCancellationToken);

        Assert.Equal("after crash", Encoding.UTF8.GetString((await ReceiveOneAsync(receiver)).Body.Span));
        Assert.Empty(await FindKeysAsync($"*{{i:{topic}/crashed-*"));
        Assert.Equal(1, await GetMuxer().GetDatabase().SortedSetLengthAsync($"{KeyPrefix}:{{t:{topic}}}:instances"));
        crashedConnection.Dispose();
    }

    [Fact]
    public async Task DisposingAnInstanceReceiver_DeletesItsKeys()
    {
        string topic = NewName("instance-keys");
        var receiver = await Transport.CreateReceiverAsync(MessageSource.InstanceSubscription(topic, "short-lived"), new ReceiverOptions(), TestCancellationToken);
        await Transport.SendAsync(MessageDestination.Topic(topic), [Message("left behind")], TestCancellationToken);
        Assert.NotEmpty(await FindKeysAsync($"*{{i:{topic}/short-lived-*"));

        await receiver.DisposeAsync();
        await Transport.SendAsync(MessageDestination.Topic(topic), [Message("after dispose")], TestCancellationToken);

        Assert.Empty(await FindKeysAsync($"*{{i:{topic}/short-lived-*"));
        Assert.Equal(0, await GetMuxer().GetDatabase().SortedSetLengthAsync($"{KeyPrefix}:{{t:{topic}}}:instances"));
    }

    [Fact]
    public async Task Delete_Subscription_RemovesHeldMessagesAndIsNotRecreatedByAConcurrentPublish()
    {
        string topic = NewName("delete-keys");
        var source = MessageSource.Subscription(topic, "held");
        var transport = (RedisMessageTransport)Transport;
        await (await CreateReceiverAsync(source)).DisposeAsync();
        await Transport.SendAsync(MessageDestination.Topic(topic), Enumerable.Range(0, 3).Select(i => Message($"a{i}") with { GroupKey = "a" }).ToList(), TestCancellationToken);
        Assert.Equal(3, await transport.GetMessageCountAsync(source));

        await Transport.DeleteAsync(source, TestCancellationToken);
        // As seen by a publisher that read the topic's subscriptions just before the delete.
        await GetMuxer().GetDatabase().SetAddAsync($"{KeyPrefix}:{{t:{topic}}}:subscriptions", source.Name);
        await Transport.SendAsync(MessageDestination.Topic(topic), [Message("while deleted") with { GroupKey = "a" }], TestCancellationToken);

        Assert.Empty(await FindKeysAsync($"*{{s:{topic}/held}}*"));
        Assert.Equal(0, await transport.GetMessageCountAsync(source));
    }

    [Fact]
    public async Task OrderedGroups_ADelayedOrRetriedMessageHoldsBackLaterOnesOfItsGroup()
    {
        var source = MessageSource.Queue(NewName("group-delay"));
        await using var receiver = await CreateReceiverAsync(source);
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [
            Message("a0", "a-0") with { GroupKey = "a", DeliverAt = DateTimeOffset.UtcNow.Add(ShortDelay) },
            Message("a1", "a-1") with { GroupKey = "a" },
            Message("b0", "b-0") with { GroupKey = "b" }
        ], TestCancellationToken);
        var stopwatch = Stopwatch.StartNew();

        var other = await ReceiveOneAsync(receiver);
        Assert.Equal("b-0", other.MessageId);
        await receiver.CompleteAsync(other, TestCancellationToken);

        var delayed = await ReceiveOneAsync(receiver, ShortDelay + DeliveryTimeout);
        Assert.Equal("a-0", delayed.MessageId);
        Assert.True(stopwatch.Elapsed >= ShortDelay * 0.75, $"Delivered after {stopwatch.Elapsed}, expected at least {ShortDelay}.");

        await receiver.AbandonAsync(delayed, ShortDelay, TestCancellationToken);
        await AssertNothingReceivedAsync(receiver, ShortDelay / 2);
        var retried = await ReceiveOneAsync(receiver, ShortDelay + DeliveryTimeout);
        Assert.Equal("a-0", retried.MessageId);
        Assert.Equal(2, retried.DeliveryCount);
        await receiver.CompleteAsync(retried, TestCancellationToken);

        var next = await ReceiveOneAsync(receiver);
        Assert.Equal("a-1", next.MessageId);
        await receiver.CompleteAsync(next, TestCancellationToken);
    }

    [Fact]
    public async Task DeadLetterQueue_ReturnsAGroupsMessagesTogether()
    {
        var source = MessageSource.Queue(NewName("dlq-groups"));
        await using var receiver = await CreateReceiverAsync(source);
        await Transport.SendAsync(MessageDestination.Queue(source.Name), Enumerable.Range(0, 3).Select(i => Message($"a{i}", $"a-{i}") with { GroupKey = "a" }).ToList(), TestCancellationToken);
        for (int i = 0; i < 3; i++)
            await receiver.DeadLetterAsync(await ReceiveOneAsync(receiver), "conformance", TestCancellationToken);

        await using var deadLetters = await CreateReceiverAsync(MessageSource.DeadLetter(source));
        var messages = await deadLetters.ReceiveAsync(10, DeliveryTimeout, TestCancellationToken);

        Assert.Equal(["a-0", "a-1", "a-2"], messages.Select(m => m.MessageId));
    }

    [Fact]
    public async Task SendBatch_LargerThanOneScriptCall_KeepsGroupOrder()
    {
        var source = MessageSource.Queue(NewName("big-batch"));
        await using var receiver = await CreateReceiverAsync(source);
        var body = new byte[3 * 1024 * 1024];
        Random.Shared.NextBytes(body);

        await Transport.SendAsync(MessageDestination.Queue(source.Name),
            Enumerable.Range(0, 5).Select(i => new OutgoingMessage { MessageId = $"a-{i}", Body = body, GroupKey = "a" }).ToList(), TestCancellationToken);

        var order = new List<string>();
        for (int i = 0; i < 5; i++)
        {
            var message = await ReceiveOneAsync(receiver);
            Assert.True(message.Body.Span.SequenceEqual(body));
            order.Add(message.MessageId);
            await receiver.CompleteAsync(message, TestCancellationToken);
        }

        Assert.Equal(["a-0", "a-1", "a-2", "a-3", "a-4"], order);
    }

    [Fact]
    public async Task ReceiveAsync_WithLargeMessages_BoundsEachBatchByBytes()
    {
        var source = MessageSource.Queue(NewName("byte-budget"));
        await using var receiver = await CreateReceiverAsync(source, ShortLease);
        var body = new byte[2 * 1024 * 1024];
        await Transport.SendAsync(MessageDestination.Queue(source.Name),
            Enumerable.Range(0, 6).Select(i => new OutgoingMessage { MessageId = $"m-{i}", Body = body }).ToList(), TestCancellationToken);

        var first = await receiver.ReceiveAsync(100, DeliveryTimeout, TestCancellationToken);
        AssertWithinReceiveBudget(first, body.Length, total: 6);

        // The first batch is left unsettled, so it is reclaimed once its leases lapse, again within the budget.
        var completed = new HashSet<string>();
        bool reclaimed = false;
        var deadline = DateTime.UtcNow + ShortLease + DeliveryTimeout;
        while (completed.Count < 6)
        {
            Assert.True(DateTime.UtcNow < deadline, $"Completed {completed.Count} of 6 messages in time.");
            var batch = await receiver.ReceiveAsync(100, TimeSpan.FromSeconds(1), TestCancellationToken);
            AssertWithinReceiveBudget(batch, body.Length, total: 6);
            foreach (var message in batch)
            {
                reclaimed |= message.DeliveryCount > 1;
                await receiver.CompleteAsync(message, TestCancellationToken);
                completed.Add(message.MessageId);
            }
        }

        Assert.True(reclaimed);
    }

    [Fact]
    public async Task ReceiveAsync_WithLargeDelayedMessages_PromotesThemWithinTheByteBudget()
    {
        var source = MessageSource.Queue(NewName("promote-budget"));
        var body = new byte[2 * 1024 * 1024];
        var delay = TimeSpan.FromMilliseconds(500);
        await Transport.SendAsync(MessageDestination.Queue(source.Name),
            Enumerable.Range(0, 6).Select(i => new OutgoingMessage { MessageId = $"m-{i}", Body = body, DeliverAt = DateTimeOffset.UtcNow.Add(delay) }).ToList(), TestCancellationToken);
        // Every message is due before the first receive, so that receive could promote all of them at once.
        await Task.Delay(delay * 2, TestCancellationToken);
        await using var receiver = await CreateReceiverAsync(source);

        var first = await receiver.ReceiveAsync(100, DeliveryTimeout, TestCancellationToken);

        AssertWithinReceiveBudget(first, body.Length, total: 6);
        Assert.True(await GetMuxer().GetDatabase().SortedSetLengthAsync($"{KeyPrefix}:{{q:{source.Name}}}:delayed") > 0,
            "Every delayed message was promoted by one receive.");
        var rest = await ReceiveAsync(receiver, 6 - first.Count);
        Assert.Equal(Enumerable.Range(0, 6).Select(i => $"m-{i}"), first.Concat(rest).Select(m => m.MessageId).Order());
    }

    [Fact]
    public async Task MaxMessageSize_FromOptions_CountsTheIdsGroupKeyAndHeadersLikeMessageSize()
    {
        var transport = Track(new RedisMessageTransport(GetMuxer(), new RedisMessageTransportOptions { KeyPrefix = KeyPrefix, LoggerFactory = Log, MaxMessageSize = 1024 }));
        Assert.Equal(1024, transport.Capabilities.MaxMessageSize);
        var headers = new Dictionary<string, string> { ["name"] = "välue" };
        var message = new OutgoingMessage { MessageId = "over", Body = new byte[1000], Headers = headers, GroupKey = "group", DeduplicationId = "dedup-id" };

        var ex = await Assert.ThrowsAsync<MessageTooLargeException>(() => transport.SendAsync(MessageDestination.Queue(NewName("limit")), [message], TestCancellationToken));

        Assert.Equal(MessageSize.Of(message), ex.Size);
        Assert.Equal(1027, ex.Size);
        Assert.Equal(1024, ex.Limit);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(256L * 1024 * 1024 + 1)]
    public void Constructor_WithMaxMessageSizeOutOfRange_Throws(long maxMessageSize)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RedisMessageTransport(GetMuxer(), new RedisMessageTransportOptions { MaxMessageSize = maxMessageSize }));
    }

    [Theory]
    [InlineData(nameof(RedisMessageTransportOptions.MinPollInterval), 0)]
    [InlineData(nameof(RedisMessageTransportOptions.MinPollInterval), -1)]
    [InlineData(nameof(RedisMessageTransportOptions.MaxPollInterval), 10)]
    [InlineData(nameof(RedisMessageTransportOptions.DeduplicationWindow), 0)]
    [InlineData(nameof(RedisMessageTransportOptions.DeduplicationWindow), -1)]
    [InlineData(nameof(RedisMessageTransportOptions.InstanceSubscriptionTimeout), 0)]
    public void Constructor_WithAnIntervalOutOfRange_Throws(string option, int milliseconds)
    {
        var value = TimeSpan.FromMilliseconds(milliseconds);
        var options = option switch
        {
            nameof(RedisMessageTransportOptions.MinPollInterval) => new RedisMessageTransportOptions { MinPollInterval = value },
            nameof(RedisMessageTransportOptions.MaxPollInterval) => new RedisMessageTransportOptions { MinPollInterval = TimeSpan.FromMilliseconds(20), MaxPollInterval = value },
            nameof(RedisMessageTransportOptions.DeduplicationWindow) => new RedisMessageTransportOptions { DeduplicationWindow = value },
            _ => new RedisMessageTransportOptions { InstanceSubscriptionTimeout = value }
        };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new RedisMessageTransport(GetMuxer(), options));
        Assert.Contains(option, ex.Message);
    }

    [Fact]
    public async Task Settle_AfterTheMessageWasRedelivered_ThrowsLeaseLost()
    {
        var source = MessageSource.Queue(NewName("fenced"));
        await using var first = await CreateReceiverAsync(source, ShortLease);
        await using var second = await CreateReceiverAsync(source, ShortLease);
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("fenced")], TestCancellationToken);

        var stale = await ReceiveOneAsync(first);
        var current = await ReceiveOneAsync(second, ShortLease + DeliveryTimeout);

        await Assert.ThrowsAsync<MessageLeaseLostException>(() => first.RenewAsync(stale, TestCancellationToken));
        await Assert.ThrowsAsync<MessageLeaseLostException>(() => first.CompleteAsync(stale, TestCancellationToken));
        await Assert.ThrowsAsync<MessageLeaseLostException>(() => first.AbandonAsync(stale, TimeSpan.Zero, TestCancellationToken));
        await second.AbandonAsync(current, TimeSpan.Zero, TestCancellationToken);
        await Assert.ThrowsAsync<MessageLeaseLostException>(() => second.CompleteAsync(current, TestCancellationToken));

        var third = await ReceiveOneAsync(second);
        Assert.Equal(3, third.DeliveryCount);
        await second.CompleteAsync(third, TestCancellationToken);
    }

    private static void AssertWithinReceiveBudget(IReadOnlyList<IncomingMessage> batch, int messageSize, int total)
    {
        long bytes = batch.Sum(m => (long)m.Body.Length);
        Assert.True(batch.Count < total, $"One receive returned all {total} messages.");
        Assert.True(bytes <= RedisMessageTransport.ReceiveBatchBytes + messageSize, $"One receive returned {bytes} bytes.");
    }

    private ConnectionMultiplexer GetMuxer()
    {
        var muxer = SharedConnection.GetMuxer(Log);
        Assert.SkipWhen(muxer is null, "RedisConnectionString is not configured.");
        return muxer;
    }

    private async Task<string[]> FindKeysAsync(string pattern)
    {
        var muxer = GetMuxer();
        var keys = await Task.WhenAll(muxer.GetEndPoints().Select(e => muxer.GetServer(e)).Where(s => !s.IsReplica)
            .Select(async s =>
            {
                var found = new List<string>();
                await foreach (var key in s.KeysAsync(pattern: pattern))
                    found.Add(key!);
                return found;
            }));

        return keys.SelectMany(k => k).ToArray();
    }
}
