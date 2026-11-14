using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;
using Foundatio.Xunit;
using Xunit;

namespace Foundatio.Tests.Messaging;

/// <summary>The in-memory transport emulating a transport with fewer capabilities, so app tests see what production would do.</summary>
public class InMemoryMessageTransportCapabilitiesTests : TestWithLoggingBase
{
    private static readonly MessageSource Queue = MessageSource.Queue("capabilities.tests");

    public InMemoryMessageTransportCapabilitiesTests(ITestOutputHelper output) : base(output)
    {
    }

    [Fact]
    public async Task Receive_WithoutOrderedGroups_HandsOutLaterMessagesOfAGroupWhileAnEarlierOneIsLeased()
    {
        await using var transport = new InMemoryMessageTransport(new TransportCapabilities());
        await SendAsync(transport, Grouped("1"), Grouped("2"), Grouped("3"));
        await using var receiver = await transport.CreateReceiverAsync(Queue, new ReceiverOptions(), TestCancellationToken);

        var first = Assert.Single(await receiver.ReceiveAsync(1, TimeSpan.FromSeconds(1), TestCancellationToken));
        var rest = await receiver.ReceiveAsync(10, TimeSpan.FromSeconds(1), TestCancellationToken);

        Assert.Equal("1", first.MessageId);
        Assert.Equal(["2", "3"], rest.Select(m => m.MessageId));
        Assert.All(rest, m => Assert.Equal("customer-1", m.GroupKey));
    }

    [Fact]
    public async Task Abandon_WithoutOrderedGroups_LetsLaterMessagesOfTheGroupOvertake()
    {
        await using var transport = new InMemoryMessageTransport(new TransportCapabilities());
        await SendAsync(transport, Grouped("1"), Grouped("2"));
        await using var receiver = await transport.CreateReceiverAsync(Queue, new ReceiverOptions(), TestCancellationToken);

        var first = Assert.Single(await receiver.ReceiveAsync(1, TimeSpan.FromSeconds(1), TestCancellationToken));
        await receiver.AbandonAsync(first, TimeSpan.FromMinutes(1), TestCancellationToken);

        Assert.Equal("2", Assert.Single(await receiver.ReceiveAsync(10, TimeSpan.FromSeconds(1), TestCancellationToken)).MessageId);
    }

    [Fact]
    public async Task Send_WithoutDeduplication_DeliversEveryMessageWithTheSameDeduplicationId()
    {
        await using var transport = new InMemoryMessageTransport(new TransportCapabilities { OrderedGroups = true });

        await SendAsync(transport, Message("1") with { DeduplicationId = "payment-7" });
        await SendAsync(transport, Message("2") with { DeduplicationId = "payment-7" });

        Assert.Equal(2, transport.GetMessageCount(Queue));
    }

    [Fact]
    public async Task Send_OverMaxMessageSize_ThrowsMessageTooLargeAndSendsNothing()
    {
        await using var transport = new InMemoryMessageTransport(new TransportCapabilities { MaxMessageSize = 100 });
        var large = Message("2") with { Body = new byte[100] };

        var error = await Assert.ThrowsAsync<MessageTooLargeException>(() => SendAsync(transport, Message("1"), large));

        Assert.Equal(100, error.Limit);
        Assert.Equal(MessageSize.Of(large), error.Size);
        Assert.Equal(0, transport.GetMessageCount(Queue));
    }

    [Fact]
    public async Task Send_DelayedBeyondTheTransportsLimits_Throws()
    {
        await using var transport = new InMemoryMessageTransport(new TransportCapabilities { MaxDelay = TimeSpan.FromMinutes(15), MaxPublishDelay = TimeSpan.Zero });
        var now = DateTimeOffset.UtcNow;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => SendAsync(transport, Message("1") with { DeliverAt = now.AddMinutes(16) }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => transport.SendAsync(MessageDestination.Topic("capabilities.events"), [Message("2") with { DeliverAt = now.AddMinutes(1) }], TestCancellationToken));
        await SendAsync(transport, Message("3") with { DeliverAt = now.AddMinutes(14) });

        Assert.Equal(1, transport.GetMessageCount(Queue));
    }

    [Fact]
    public async Task Abandon_WithADelayBeyondMaxRetryDelay_ThrowsAndKeepsTheLease()
    {
        await using var transport = new InMemoryMessageTransport(new TransportCapabilities { MaxRetryDelay = TimeSpan.FromSeconds(30) });
        await SendAsync(transport, Message("1"));
        await using var receiver = await transport.CreateReceiverAsync(Queue, new ReceiverOptions(), TestCancellationToken);
        var message = Assert.Single(await receiver.ReceiveAsync(1, TimeSpan.FromSeconds(1), TestCancellationToken));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => receiver.AbandonAsync(message, TimeSpan.FromMinutes(1), TestCancellationToken));

        Assert.Empty(await receiver.ReceiveAsync(1, TimeSpan.FromMilliseconds(100), TestCancellationToken));
        await receiver.AbandonAsync(message, TimeSpan.Zero, TestCancellationToken);
        Assert.Equal(2, Assert.Single(await receiver.ReceiveAsync(1, TimeSpan.FromSeconds(1), TestCancellationToken)).DeliveryCount);
    }

    [Fact]
    public async Task CreateReceiver_WithALeaseBeyondMaxLeaseDuration_GrantsAtMostTheLimit()
    {
        await using var transport = new InMemoryMessageTransport(new TransportCapabilities { MaxLeaseDuration = TimeSpan.FromMilliseconds(200) });
        await SendAsync(transport, Message("1"));
        await using var receiver = await transport.CreateReceiverAsync(Queue, new ReceiverOptions { LeaseDuration = TimeSpan.FromHours(1) }, TestCancellationToken);

        Assert.Single(await receiver.ReceiveAsync(1, TimeSpan.FromSeconds(1), TestCancellationToken));
        var redelivered = Assert.Single(await receiver.ReceiveAsync(1, TimeSpan.FromSeconds(10), TestCancellationToken));

        Assert.Equal(2, redelivered.DeliveryCount);
    }

    [Fact]
    public async Task Receive_ReturnsAtMostMaxReceiveBatchSize()
    {
        await using var transport = new InMemoryMessageTransport(new TransportCapabilities { MaxReceiveBatchSize = 2 });
        await SendAsync(transport, Message("1"), Message("2"), Message("3"));
        await using var receiver = await transport.CreateReceiverAsync(Queue, new ReceiverOptions(), TestCancellationToken);

        Assert.Equal(2, (await receiver.ReceiveAsync(10, TimeSpan.FromSeconds(1), TestCancellationToken)).Count);
    }

    [Fact]
    public async Task Lease_WithoutLeaseExpiration_IsHeldUntilTheReceiverIsDisposed()
    {
        await using var transport = new InMemoryMessageTransport(new TransportCapabilities { LeaseExpiration = false });
        await SendAsync(transport, Message("1"));
        var options = new ReceiverOptions { LeaseDuration = TimeSpan.FromMilliseconds(50) };
        var holder = await transport.CreateReceiverAsync(Queue, options, TestCancellationToken);
        await using var other = await transport.CreateReceiverAsync(Queue, options, TestCancellationToken);
        var held = Assert.Single(await holder.ReceiveAsync(1, TimeSpan.FromSeconds(1), TestCancellationToken));

        Assert.Empty(await other.ReceiveAsync(1, TimeSpan.FromMilliseconds(300), TestCancellationToken));
        await holder.RenewAsync(held, TestCancellationToken);
        await holder.DisposeAsync();

        Assert.Equal(2, Assert.Single(await other.ReceiveAsync(1, TimeSpan.FromSeconds(1), TestCancellationToken)).DeliveryCount);
    }

    public static TheoryData<TransportCapabilities> CapabilitiesThatCannotBeEmulated => new()
    {
        new TransportCapabilities { MaxSendBatchSize = 0 },
        new TransportCapabilities { MaxReceiveBatchSize = 0 },
        new TransportCapabilities { MaxDelay = TimeSpan.FromSeconds(-1) },
        new TransportCapabilities { MaxPublishDelay = TimeSpan.FromSeconds(-1) },
        new TransportCapabilities { MaxRetryDelay = TimeSpan.FromSeconds(-1) },
        new TransportCapabilities { MinRetryDelay = TimeSpan.FromSeconds(-1) },
        new TransportCapabilities { MinRetryDelay = TimeSpan.FromMinutes(2), MaxRetryDelay = TimeSpan.FromMinutes(1) },
        new TransportCapabilities { MaxMessageSize = 0 },
        new TransportCapabilities { MaxLeaseDuration = TimeSpan.Zero }
    };

    [Theory]
    [MemberData(nameof(CapabilitiesThatCannotBeEmulated))]
    public void Constructor_WithCapabilitiesItCannotEmulate_ThrowsArgumentException(TransportCapabilities capabilities)
    {
        var error = Assert.Throws<ArgumentException>(() => new InMemoryMessageTransport(capabilities));
        Assert.Equal("capabilities", error.ParamName);
    }

    [Fact]
    public async Task Messaging_OnATransportWithoutOrderedGroups_HandlesAGroupsMessagesConcurrently()
    {
        await using var transport = new InMemoryMessageTransport(new TransportCapabilities());
        var allRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int running = 0;
        await using var host = await MessagingTestHost.StartAsync(transport, Log, "tests", m => m.AddConsumer<GroupedCommand>(async (_, _, ct) =>
        {
            if (Interlocked.Increment(ref running) == 3)
                allRunning.TrySetResult();

            await allRunning.Task.WaitAsync(ct);
        }, new HandlerOptions { MaxConcurrency = 3 }));

        await host.Client.SendBatchAsync(Enumerable.Range(1, 3).Select(i => new GroupedCommand(i)), new SendOptions { GroupKey = "customer-1" }, TestCancellationToken);

        await allRunning.Task.WaitAsync(TimeSpan.FromSeconds(10), TestCancellationToken);
        Assert.True(await transport.WaitUntilIdleAsync(TimeSpan.FromSeconds(10), TestCancellationToken));
    }

    private Task SendAsync(InMemoryMessageTransport transport, params OutgoingMessage[] messages)
        => transport.SendAsync(MessageDestination.Queue(Queue.Name), messages, TestCancellationToken);

    private static OutgoingMessage Message(string id) => new() { MessageId = id, Body = new byte[] { 1 } };

    private static OutgoingMessage Grouped(string id) => Message(id) with { GroupKey = "customer-1" };

    public record GroupedCommand(int Id);
}
