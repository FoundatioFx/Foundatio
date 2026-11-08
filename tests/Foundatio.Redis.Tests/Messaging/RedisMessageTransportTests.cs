using System;
using System.Collections.Generic;
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

        Assert.Empty(await FindKeysAsync($"*{{i:{topic}/short-lived-*"));
        Assert.Equal(0, await GetMuxer().GetDatabase().SortedSetLengthAsync($"{KeyPrefix}:{{t:{topic}}}:instances"));
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
