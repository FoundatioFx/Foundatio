using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Foundatio.Messaging.Transport;
using Foundatio.Xunit;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Foundatio.Tests.Messaging;

/// <summary>How the in-memory transport picks what a receive hands out, beyond what the conformance suite checks.</summary>
public class InMemoryMessageTransportReceiveTests : TestWithLoggingBase
{
    private static readonly MessageSource Queue = MessageSource.Queue("receive.tests");

    public InMemoryMessageTransportReceiveTests(ITestOutputHelper output) : base(output)
    {
    }

    [Fact]
    public async Task Receive_OneAtATimeBehindALargeLeasedBacklog_DoesNotRescanTheBacklog()
    {
        const int count = 100_000;
        await using var transport = new InMemoryMessageTransport();
        await SendAsync(transport, Enumerable.Range(0, count).Select(i => Message(i.ToString())).ToArray());
        await using var receiver = await transport.CreateReceiverAsync(Queue, new ReceiverOptions { LeaseDuration = TimeSpan.FromMinutes(5) }, TestCancellationToken);

        // Every message stays leased, so each receive finds all the earlier ones ahead of the next available one.
        var stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < count; i++)
        {
            var message = Assert.Single(await receiver.ReceiveAsync(1, TimeSpan.Zero, TestCancellationToken));
            Assert.Equal(i.ToString(), message.MessageId);
        }

        _logger.LogInformation("Received {Count} messages one at a time in {Elapsed}", count, stopwatch.Elapsed);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Receiving took {stopwatch.Elapsed}; a receive should not look at every leased message.");
        Assert.Empty(await receiver.ReceiveAsync(10, TimeSpan.Zero, TestCancellationToken));
    }

    [Fact]
    public async Task Receive_InAnOrderedGroup_HandsOutTheRestOfTheGroupTogetherOnceTheLeasedMessageIsSettled()
    {
        await using var transport = new InMemoryMessageTransport();
        await SendAsync(transport, Grouped("a0"), Message("x0"), Grouped("a1"), Grouped("a2"), Message("x1"));
        await using var receiver = await transport.CreateReceiverAsync(Queue, new ReceiverOptions(), TestCancellationToken);

        var first = Assert.Single(await receiver.ReceiveAsync(1, TimeSpan.Zero, TestCancellationToken));
        Assert.Equal("a0", first.MessageId);
        var others = await receiver.ReceiveAsync(10, TimeSpan.Zero, TestCancellationToken);
        Assert.Equal(["x0", "x1"], others.Select(m => m.MessageId));
        Assert.Empty(await receiver.ReceiveAsync(10, TimeSpan.Zero, TestCancellationToken));

        await receiver.CompleteAsync(first, TestCancellationToken);
        var rest = await receiver.ReceiveAsync(10, TimeSpan.Zero, TestCancellationToken);

        Assert.Equal(["a1", "a2"], rest.Select(m => m.MessageId));
    }

    [Fact]
    public async Task Receive_WhenAnOrderedGroupsFirstMessageIsReleasedWithADelay_HoldsTheGroupUntilItIsDue()
    {
        await using var transport = new InMemoryMessageTransport();
        await SendAsync(transport, Grouped("a0"), Grouped("a1"), Message("x0"));
        await using var receiver = await transport.CreateReceiverAsync(Queue, new ReceiverOptions(), TestCancellationToken);

        var batch = await receiver.ReceiveAsync(10, TimeSpan.Zero, TestCancellationToken);
        Assert.Equal(["a0", "a1", "x0"], batch.Select(m => m.MessageId));
        await receiver.AbandonAsync(batch[0], TimeSpan.FromMilliseconds(300), TestCancellationToken);
        await receiver.AbandonAsync(batch[1], TimeSpan.Zero, TestCancellationToken);
        await receiver.CompleteAsync(batch[2], TestCancellationToken);
        await SendAsync(transport, Message("x1"));

        Assert.Equal("x1", Assert.Single(await receiver.ReceiveAsync(10, TimeSpan.Zero, TestCancellationToken)).MessageId);
        var redelivered = await receiver.ReceiveAsync(10, TimeSpan.FromSeconds(5), TestCancellationToken);

        Assert.Equal(["a0", "a1"], redelivered.Select(m => m.MessageId));
        Assert.All(redelivered, m => Assert.Equal(2, m.DeliveryCount));
    }

    [Fact]
    public async Task Receive_WhenALaterMessageOfAGroupOutlivesItsLease_RedeliversItOnlyOnceTheEarlierOneIsSettled()
    {
        await using var transport = new InMemoryMessageTransport();
        await SendAsync(transport, Grouped("a0"), Grouped("a1"));
        await using var receiver = await transport.CreateReceiverAsync(Queue, new ReceiverOptions { LeaseDuration = TimeSpan.FromMilliseconds(300) }, TestCancellationToken);
        var batch = await receiver.ReceiveAsync(10, TimeSpan.Zero, TestCancellationToken);
        Assert.Equal(["a0", "a1"], batch.Select(m => m.MessageId));

        var deadline = DateTime.UtcNow.AddMilliseconds(900);
        while (DateTime.UtcNow < deadline)
        {
            await receiver.RenewAsync(batch[0], TestCancellationToken);
            Assert.Empty(await receiver.ReceiveAsync(10, TimeSpan.FromMilliseconds(100), TestCancellationToken));
        }

        await receiver.CompleteAsync(batch[0], TestCancellationToken);
        var redelivered = Assert.Single(await receiver.ReceiveAsync(10, TimeSpan.Zero, TestCancellationToken));

        Assert.Equal("a1", redelivered.MessageId);
        Assert.Equal(2, redelivered.DeliveryCount);
        await Assert.ThrowsAsync<MessageLeaseLostException>(() => receiver.CompleteAsync(batch[1], TestCancellationToken));
    }

    [Fact]
    public async Task Renew_AfterTheLeaseEndedButBeforeAnotherReceive_KeepsTheMessageLeased()
    {
        await using var transport = new InMemoryMessageTransport();
        await SendAsync(transport, Message("1"), Grouped("a0"), Grouped("a1"));
        await using var receiver = await transport.CreateReceiverAsync(Queue, new ReceiverOptions { LeaseDuration = TimeSpan.FromMilliseconds(200) }, TestCancellationToken);
        var batch = await receiver.ReceiveAsync(10, TimeSpan.Zero, TestCancellationToken);
        Assert.Equal(["1", "a0", "a1"], batch.Select(m => m.MessageId));

        await Task.Delay(400, TestCancellationToken);
        Assert.Equal(["1"], (await receiver.ReceiveAsync(1, TimeSpan.Zero, TestCancellationToken)).Select(m => m.MessageId));
        foreach (var message in batch.Skip(1))
            await receiver.RenewAsync(message, TestCancellationToken);

        Assert.Empty(await receiver.ReceiveAsync(10, TimeSpan.Zero, TestCancellationToken));
        var stats = await transport.GetStatsAsync(Queue, TestCancellationToken);
        Assert.Equal((0L, 3L), (stats!.Queued, stats.InFlight));
    }

    private Task SendAsync(InMemoryMessageTransport transport, params OutgoingMessage[] messages)
        => transport.SendAsync(MessageDestination.Queue(Queue.Name), messages, TestCancellationToken);

    private static OutgoingMessage Message(string id) => new() { MessageId = id, Body = new byte[] { 1 } };

    private static OutgoingMessage Grouped(string id) => Message(id) with { GroupKey = "customer-1" };
}
