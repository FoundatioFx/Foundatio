using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;
using Foundatio.Xunit;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Foundatio.Tests.Messaging;

/// <summary>
/// The conformance suite every <see cref="IMessageTransport"/> must pass. Derive from it, implement
/// <see cref="CreateTransport"/>, and every test runs against your broker. Tests use unique names so they can run
/// in parallel against a shared broker; tests for capabilities a transport does not declare are skipped.
/// </summary>
/// <example>
/// <code>
/// public class RedisMessageTransportTests(ITestOutputHelper output) : MessageTransportTestsBase(output)
/// {
///     protected override IMessageTransport CreateTransport() => new RedisMessageTransport(...);
/// }
/// </code>
/// </example>
public abstract class MessageTransportTestsBase : TestWithLoggingBase
{
    private readonly List<IMessageTransport> _transports = [];

    protected MessageTransportTestsBase(ITestOutputHelper output) : base(output)
    {
    }

    /// <summary>Creates a transport connected to the broker under test. Called one or more times per test.</summary>
    protected abstract IMessageTransport CreateTransport();

    /// <summary>The longest a test waits for a message the broker should deliver.</summary>
    protected virtual TimeSpan DeliveryTimeout => TimeSpan.FromSeconds(10);

    /// <summary>The lease used by tests that wait for leases to expire.</summary>
    protected virtual TimeSpan ShortLease => TimeSpan.FromSeconds(2);

    /// <summary>The delay used by delayed-delivery tests.</summary>
    protected virtual TimeSpan ShortDelay => TimeSpan.FromSeconds(2);

    protected IMessageTransport Transport => field ??= Track(CreateTransport());

    [Fact]
    public async Task SendThenReceive_RoundTripsIdBodyAndHeaders()
    {
        var source = MessageSource.Queue(NewName("roundtrip"));
        await using var receiver = await CreateReceiverAsync(source);
        var headers = new Dictionary<string, string> { ["message-type"] = "conformance.test", ["unicode"] = "héllo wörld ✓", ["empty"] = "" };

        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("payload", "id-1", headers) with { GroupKey = "group-1" }], TestCancellationToken);

        var message = await ReceiveOneAsync(receiver);
        Assert.Equal("id-1", message.MessageId);
        Assert.Equal("group-1", message.GroupKey);
        Assert.Equal("payload", Encoding.UTF8.GetString(message.Body.Span));
        Assert.Equal(1, message.DeliveryCount);
        foreach (var (key, value) in headers)
            Assert.Equal(value, message.Headers[key]);

        await receiver.CompleteAsync(message, TestCancellationToken);
    }

    [Fact]
    public async Task Receive_WhenEmpty_ReturnsEmptyWithinMaxWait()
    {
        await using var receiver = await CreateReceiverAsync(MessageSource.Queue(NewName("empty")));
        var stopwatch = Stopwatch.StartNew();

        var messages = await receiver.ReceiveAsync(10, TimeSpan.FromSeconds(1), TestCancellationToken);

        Assert.Empty(messages);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1) + TimeSpan.FromSeconds(4), $"ReceiveAsync took {stopwatch.Elapsed} with a 1s maxWait.");
    }

    [Fact]
    public async Task Receive_ReturnsAtMostMaxMessages()
    {
        var source = MessageSource.Queue(NewName("max"));
        await using var receiver = await CreateReceiverAsync(source);
        await Transport.SendAsync(MessageDestination.Queue(source.Name), Enumerable.Range(0, 5).Select(i => Message($"m{i}")).ToList(), TestCancellationToken);

        var first = await receiver.ReceiveAsync(2, DeliveryTimeout, TestCancellationToken);
        Assert.InRange(first.Count, 1, 2);

        var all = new List<IncomingMessage>(first);
        all.AddRange(await ReceiveAsync(receiver, 5 - first.Count));
        Assert.Equal(5, all.Select(m => m.MessageId).Distinct().Count());
    }

    [Fact]
    public async Task Send_BeforeAnyReceiverExists_KeepsTheMessage()
    {
        var source = MessageSource.Queue(NewName("early"));
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("early")], TestCancellationToken);

        await using var receiver = await CreateReceiverAsync(source);
        var message = await ReceiveOneAsync(receiver);
        Assert.Equal("early", Encoding.UTF8.GetString(message.Body.Span));
    }

    [Fact]
    public async Task Complete_RemovesTheMessage()
    {
        var source = MessageSource.Queue(NewName("complete"));
        await using var receiver = await CreateReceiverAsync(source, ShortLease);
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("done")], TestCancellationToken);

        await receiver.CompleteAsync(await ReceiveOneAsync(receiver), TestCancellationToken);

        await AssertNothingReceivedAsync(receiver, ShortLease + TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Abandon_RedeliversWithIncrementedDeliveryCount()
    {
        var source = MessageSource.Queue(NewName("abandon"));
        await using var receiver = await CreateReceiverAsync(source);
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("again", "id-again")], TestCancellationToken);

        var first = await ReceiveOneAsync(receiver);
        await receiver.AbandonAsync(first, TimeSpan.Zero, TestCancellationToken);
        var second = await ReceiveOneAsync(receiver);

        Assert.Equal("id-again", second.MessageId);
        Assert.Equal(first.DeliveryCount + 1, second.DeliveryCount);
        await receiver.CompleteAsync(second, TestCancellationToken);
    }

    [Fact]
    public async Task Abandon_WithDelay_WaitsBeforeRedelivering()
    {
        Assert.SkipWhen(Transport.Capabilities.MaxDelay == TimeSpan.Zero, "The transport does not support delays.");
        var source = MessageSource.Queue(NewName("abandon-delay"));
        await using var receiver = await CreateReceiverAsync(source);
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("later")], TestCancellationToken);

        var first = await ReceiveOneAsync(receiver);
        var stopwatch = Stopwatch.StartNew();
        await receiver.AbandonAsync(first, ShortDelay, TestCancellationToken);
        var second = await ReceiveOneAsync(receiver, ShortDelay + DeliveryTimeout);

        Assert.True(stopwatch.Elapsed >= ShortDelay * 0.75, $"Redelivered after {stopwatch.Elapsed}, expected at least {ShortDelay}.");
        await receiver.CompleteAsync(second, TestCancellationToken);
    }

    [Fact]
    public async Task UnsettledMessage_IsRedeliveredAfterLeaseExpires()
    {
        Assert.SkipUnless(Transport.Capabilities.LeaseExpiration, "The transport does not expire leases.");
        var source = MessageSource.Queue(NewName("lease"));
        await using var receiver = await CreateReceiverAsync(source, ShortLease);
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("leased")], TestCancellationToken);

        var first = await ReceiveOneAsync(receiver);
        var second = await ReceiveOneAsync(receiver, ShortLease + DeliveryTimeout);

        Assert.Equal(first.MessageId, second.MessageId);
        Assert.True(second.DeliveryCount > first.DeliveryCount);
        await receiver.CompleteAsync(second, TestCancellationToken);
    }

    [Fact]
    public async Task Renew_ExtendsTheLease()
    {
        Assert.SkipUnless(Transport.Capabilities.LeaseExpiration, "The transport does not expire leases.");
        var source = MessageSource.Queue(NewName("renew"));
        await using var receiver = await CreateReceiverAsync(source, ShortLease);
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("renewed")], TestCancellationToken);

        var message = await ReceiveOneAsync(receiver);
        await Task.Delay(ShortLease / 2, TestCancellationToken);
        await receiver.RenewAsync(message, TestCancellationToken);

        await AssertNothingReceivedAsync(receiver, ShortLease * 0.75);
        await receiver.CompleteAsync(message, TestCancellationToken);
    }

    [Fact]
    public async Task DisposingAReceiver_ReleasesItsUnsettledMessages()
    {
        var source = MessageSource.Queue(NewName("dispose"));
        var receiver = await CreateReceiverAsync(source, ShortLease);
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("orphan")], TestCancellationToken);
        var first = await ReceiveOneAsync(receiver);
        await receiver.DisposeAsync();

        await using var next = await CreateReceiverAsync(source, ShortLease);
        var message = await ReceiveOneAsync(next, ShortLease + DeliveryTimeout);
        Assert.Equal("orphan", Encoding.UTF8.GetString(message.Body.Span));
        Assert.True(message.DeliveryCount > first.DeliveryCount, $"DeliveryCount stayed at {message.DeliveryCount} after redelivery.");
        await next.CompleteAsync(message, TestCancellationToken);
    }

    [Fact]
    public async Task StaleSettlement_FailsWithLeaseLostAndLeavesTheNewDeliveryIntact()
    {
        Assert.SkipUnless(Transport.Capabilities.LeaseExpiration, "The transport does not expire leases.");
        var source = MessageSource.Queue(NewName("stale"));
        await using var first = await CreateReceiverAsync(source, ShortLease);
        await using var second = await CreateReceiverAsync(source, ShortLease);
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("contested")], TestCancellationToken);

        var stale = await ReceiveOneAsync(first);
        var current = await ReceiveOneAsync(second, ShortLease + DeliveryTimeout);

        await AssertLeaseLostOrIgnoredAsync(() => first.RenewAsync(stale, TestCancellationToken));
        await AssertLeaseLostOrIgnoredAsync(() => first.CompleteAsync(stale, TestCancellationToken));
        await AssertLeaseLostOrIgnoredAsync(() => first.AbandonAsync(stale, TimeSpan.Zero, TestCancellationToken));
        await second.CompleteAsync(current, TestCancellationToken);
        await AssertNothingReceivedAsync(second, ShortLease + TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task DeadLetter_MovesTheMessageToTheDeadLetterQueue()
    {
        var source = MessageSource.Queue(NewName("dlq"));
        await using var receiver = await CreateReceiverAsync(source, ShortLease);
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("poison", "id-poison", new Dictionary<string, string> { ["custom"] = "kept" })], TestCancellationToken);

        await receiver.DeadLetterAsync(await ReceiveOneAsync(receiver), "conformance reason", TestCancellationToken);

        await using var deadLetters = await CreateReceiverAsync(MessageSource.DeadLetter(source));
        var deadLetter = await ReceiveOneAsync(deadLetters);
        Assert.Equal("id-poison", deadLetter.MessageId);
        Assert.Equal("poison", Encoding.UTF8.GetString(deadLetter.Body.Span));
        Assert.Equal("kept", deadLetter.Headers["custom"]);
        Assert.Equal("conformance reason", deadLetter.Headers[KnownHeaders.DeadLetterReason]);
        await deadLetters.CompleteAsync(deadLetter, TestCancellationToken);

        await AssertNothingReceivedAsync(receiver, ShortLease + TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task DeadLetter_FromASubscription_GoesToThatSubscriptionsDeadLetterQueue()
    {
        string topic = NewName("dlq-topic");
        var source = MessageSource.Subscription(topic, "conformance");
        await using var receiver = await CreateReceiverAsync(source);
        await Transport.SendAsync(MessageDestination.Topic(topic), [Message("bad event", "id-event")], TestCancellationToken);

        await receiver.DeadLetterAsync(await ReceiveOneAsync(receiver), "bad event", TestCancellationToken);

        await using var deadLetters = await CreateReceiverAsync(MessageSource.DeadLetter(source));
        var deadLetter = await ReceiveOneAsync(deadLetters);
        Assert.Equal("id-event", deadLetter.MessageId);
        await deadLetters.CompleteAsync(deadLetter, TestCancellationToken);
    }

    [Fact]
    public async Task DeliverAt_DelaysDelivery()
    {
        Assert.SkipWhen(Transport.Capabilities.MaxDelay == TimeSpan.Zero, "The transport does not support delays.");
        var source = MessageSource.Queue(NewName("delay"));
        await using var receiver = await CreateReceiverAsync(source);
        var stopwatch = Stopwatch.StartNew();

        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("scheduled") with { DeliverAt = DateTimeOffset.UtcNow.Add(ShortDelay) }], TestCancellationToken);

        var message = await ReceiveOneAsync(receiver, ShortDelay + DeliveryTimeout);
        Assert.True(stopwatch.Elapsed >= ShortDelay * 0.75, $"Delivered after {stopwatch.Elapsed}, expected at least {ShortDelay}.");
        await receiver.CompleteAsync(message, TestCancellationToken);
    }

    [Fact]
    public async Task DeliverAt_DelaysPublishedMessages()
    {
        Assert.SkipWhen(Transport.Capabilities.MaxDelay == TimeSpan.Zero, "The transport does not support delays.");
        string topic = NewName("publish-delay");
        await using var receiver = await CreateReceiverAsync(MessageSource.Subscription(topic, "delayed"));
        var stopwatch = Stopwatch.StartNew();

        await Transport.SendAsync(MessageDestination.Topic(topic), [Message("later event") with { DeliverAt = DateTimeOffset.UtcNow.Add(ShortDelay) }], TestCancellationToken);

        var message = await ReceiveOneAsync(receiver, ShortDelay + DeliveryTimeout);
        Assert.True(stopwatch.Elapsed >= ShortDelay * 0.75, $"Delivered after {stopwatch.Elapsed}, expected at least {ShortDelay}.");
        Assert.Equal(1, message.DeliveryCount);
        await receiver.CompleteAsync(message, TestCancellationToken);
    }

    [Fact]
    public async Task CompetingReceivers_EachMessageIsDeliveredOnce()
    {
        var source = MessageSource.Queue(NewName("compete"));
        await using var first = await CreateReceiverAsync(source);
        await using var second = await CreateReceiverAsync(source);
        await Transport.SendAsync(MessageDestination.Queue(source.Name), Enumerable.Range(0, 20).Select(i => Message($"m{i}", $"id-{i}")).ToList(), TestCancellationToken);

        var received = new ConcurrentBag<string>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestCancellationToken);
        timeout.CancelAfter(DeliveryTimeout * 2);

        async Task DrainAsync(IMessageReceiver receiver)
        {
            while (received.Count < 20 && !timeout.IsCancellationRequested)
            {
                foreach (var message in await receiver.ReceiveAsync(5, TimeSpan.FromSeconds(1), timeout.Token))
                {
                    received.Add(message.MessageId);
                    await receiver.CompleteAsync(message, TestCancellationToken);
                }
            }
        }

        await Task.WhenAll(DrainAsync(first), DrainAsync(second));

        Assert.Equal(20, received.Count);
        Assert.Equal(20, received.Distinct().Count());
    }

    [Fact]
    public async Task Publish_DeliversACopyToEverySubscription()
    {
        string topic = NewName("fanout");
        await using var billing = await CreateReceiverAsync(MessageSource.Subscription(topic, "billing"));
        await using var shipping = await CreateReceiverAsync(MessageSource.Subscription(topic, "shipping"));

        await Transport.SendAsync(MessageDestination.Topic(topic), Enumerable.Range(0, 3).Select(i => Message($"e{i}", $"id-{i}")).ToList(), TestCancellationToken);

        foreach (var receiver in new[] { billing, shipping })
        {
            var messages = await ReceiveAsync(receiver, 3);
            Assert.Equal(["id-0", "id-1", "id-2"], messages.Select(m => m.MessageId).Order());
            foreach (var message in messages)
                await receiver.CompleteAsync(message, TestCancellationToken);
        }
    }

    [Fact]
    public async Task Subscription_KeepsMessagesWhileNoReceiverIsOpen()
    {
        string topic = NewName("durable");
        var source = MessageSource.Subscription(topic, "offline");
        await (await CreateReceiverAsync(source)).DisposeAsync();

        await Transport.SendAsync(MessageDestination.Topic(topic), [Message("while offline")], TestCancellationToken);

        await using var receiver = await CreateReceiverAsync(source);
        var message = await ReceiveOneAsync(receiver);
        Assert.Equal("while offline", Encoding.UTF8.GetString(message.Body.Span));
        await receiver.CompleteAsync(message, TestCancellationToken);
    }

    [Fact]
    public async Task Subscription_CompetingReceiversShareMessages()
    {
        string topic = NewName("sub-compete");
        var source = MessageSource.Subscription(topic, "workers");
        await using var first = await CreateReceiverAsync(source);
        await using var second = await CreateReceiverAsync(source);
        await Transport.SendAsync(MessageDestination.Topic(topic), Enumerable.Range(0, 10).Select(i => Message($"e{i}", $"id-{i}")).ToList(), TestCancellationToken);

        var received = new ConcurrentBag<string>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestCancellationToken);
        timeout.CancelAfter(DeliveryTimeout * 2);

        async Task DrainAsync(IMessageReceiver receiver)
        {
            while (received.Count < 10 && !timeout.IsCancellationRequested)
            {
                foreach (var message in await receiver.ReceiveAsync(3, TimeSpan.FromSeconds(1), timeout.Token))
                {
                    received.Add(message.MessageId);
                    await receiver.CompleteAsync(message, TestCancellationToken);
                }
            }
        }

        await Task.WhenAll(DrainAsync(first), DrainAsync(second));
        Assert.Equal(10, received.Distinct().Count());
        Assert.Equal(10, received.Count);
    }

    [Fact]
    public async Task InstanceSubscriptions_EachGetACopyOnlyWhileOpen()
    {
        string topic = NewName("instances");
        var first = await CreateReceiverAsync(MessageSource.InstanceSubscription(topic, "conformance"));
        await using var second = await CreateReceiverAsync(MessageSource.InstanceSubscription(topic, "conformance"));

        await Transport.SendAsync(MessageDestination.Topic(topic), [Message("to all", "id-all")], TestCancellationToken);
        foreach (var receiver in new[] { first, second })
        {
            var message = await ReceiveOneAsync(receiver);
            Assert.Equal("id-all", message.MessageId);
            await receiver.CompleteAsync(message, TestCancellationToken);
        }

        await first.DisposeAsync();
        await Transport.SendAsync(MessageDestination.Topic(topic), [Message("to remaining", "id-remaining")], TestCancellationToken);
        var remaining = await ReceiveOneAsync(second);
        Assert.Equal("id-remaining", remaining.MessageId);
        await second.CompleteAsync(remaining, TestCancellationToken);

        await using var late = await CreateReceiverAsync(MessageSource.InstanceSubscription(topic, "conformance"));
        await AssertNothingReceivedAsync(late, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public Task Publish_WithoutSubscriptions_Succeeds()
    {
        return Transport.SendAsync(MessageDestination.Topic(NewName("nobody")), [Message("unheard")], TestCancellationToken);
    }

    [Fact]
    public async Task SendBatch_AtTheMaximumBatchSize_DeliversEveryMessage()
    {
        int count = Math.Min(Transport.Capabilities.MaxSendBatchSize, 100);
        var source = MessageSource.Queue(NewName("batch"));
        await using var receiver = await CreateReceiverAsync(source);

        await Transport.SendAsync(MessageDestination.Queue(source.Name), Enumerable.Range(0, count).Select(i => Message($"m{i}", $"id-{i}")).ToList(), TestCancellationToken);

        var messages = await ReceiveAsync(receiver, count);
        Assert.Equal(count, messages.Select(m => m.MessageId).Distinct().Count());
    }

    [Fact]
    public async Task Names_WithEveryAllowedCharacter_Work()
    {
        string name = NewName("Foundatio.Conformance_Name-v1");
        await using var queue = await CreateReceiverAsync(MessageSource.Queue(name));
        await using var subscription = await CreateReceiverAsync(MessageSource.Subscription(name, "Service.Name_v2-x"));

        await Transport.SendAsync(MessageDestination.Queue(name), [Message("queued")], TestCancellationToken);
        await Transport.SendAsync(MessageDestination.Topic(name), [Message("published")], TestCancellationToken);

        Assert.Equal("queued", Encoding.UTF8.GetString((await ReceiveOneAsync(queue)).Body.Span));
        Assert.Equal("published", Encoding.UTF8.GetString((await ReceiveOneAsync(subscription)).Body.Span));
    }

    [Fact]
    public async Task EmptyAndLargeBodies_RoundTrip()
    {
        int largeSize = (int)Math.Min(Transport.Capabilities.MaxMessageSize / 2 ?? 128 * 1024, 128 * 1024);
        var large = new byte[largeSize];
        Random.Shared.NextBytes(large);
        var source = MessageSource.Queue(NewName("bodies"));
        await using var receiver = await CreateReceiverAsync(source);

        await Transport.SendAsync(MessageDestination.Queue(source.Name), [
            new OutgoingMessage { MessageId = "empty", Body = ReadOnlyMemory<byte>.Empty },
            new OutgoingMessage { MessageId = "large", Body = large }
        ], TestCancellationToken);

        var messages = (await ReceiveAsync(receiver, 2)).ToDictionary(m => m.MessageId);
        Assert.Equal(0, messages["empty"].Body.Length);
        Assert.True(messages["large"].Body.Span.SequenceEqual(large));
    }

    [Fact]
    public async Task OrderedGroups_AreDeliveredInOrderOneAtATime()
    {
        Assert.SkipUnless(Transport.Capabilities.OrderedGroups, "The transport does not order groups.");
        var source = MessageSource.Queue(NewName("groups"));
        await using var receiver = await CreateReceiverAsync(source);
        await Transport.SendAsync(MessageDestination.Queue(source.Name),
            Enumerable.Range(0, 4).Select(i => Message($"a{i}", $"a-{i}") with { GroupKey = "a" }).Append(Message("b0", "b-0") with { GroupKey = "b" }).ToList(),
            TestCancellationToken);

        var order = new List<string>();
        var deadline = DateTime.UtcNow + DeliveryTimeout;
        while (order.Count(id => id.StartsWith("a-")) < 4 && DateTime.UtcNow < deadline)
        {
            var batch = await receiver.ReceiveAsync(10, TimeSpan.FromSeconds(1), TestCancellationToken);
            Assert.True(batch.Count(m => m.GroupKey == "a") <= 1, "More than one message of a group was in flight at once.");
            foreach (var message in batch)
            {
                order.Add(message.MessageId);
                await receiver.CompleteAsync(message, TestCancellationToken);
            }
        }

        Assert.Equal(["a-0", "a-1", "a-2", "a-3"], order.Where(id => id.StartsWith("a-")));
        Assert.Contains("b-0", order);
    }

    [Fact]
    public async Task Deduplication_DropsMessagesWithASeenId()
    {
        Assert.SkipUnless(Transport.Capabilities.Deduplication, "The transport does not deduplicate.");
        var source = MessageSource.Queue(NewName("dedupe"));
        await using var receiver = await CreateReceiverAsync(source, ShortLease);

        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("one", "first") with { DeduplicationId = "same" }], TestCancellationToken);
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("two", "second") with { DeduplicationId = "same" }], TestCancellationToken);

        var message = await ReceiveOneAsync(receiver);
        Assert.Equal("first", message.MessageId);
        await receiver.CompleteAsync(message, TestCancellationToken);
        await AssertNothingReceivedAsync(receiver, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Messaging_SentMessageIsHandledOnceByCompetingServices()
    {
        string name = NewName("cmd");
        int handled = 0;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<FoundatioMessagingBuilder> configure = m => m.MapMessage<ConformanceMessage>(name).AddConsumer<ConformanceMessage>((_, _, _) =>
        {
            if (Interlocked.Increment(ref handled) == 5)
                done.TrySetResult();
            return Task.CompletedTask;
        });

        await using var first = await MessagingTestHost.StartAsync(Transport, Log, "worker", configure);
        await using var second = await MessagingTestHost.StartAsync(Transport, Log, "worker", configure);
        for (int i = 0; i < 5; i++)
            await first.Client.SendAsync(new ConformanceMessage(i), cancellationToken: TestCancellationToken);

        await done.Task.WaitAsync(DeliveryTimeout, TestCancellationToken);
        await Task.Delay(500, TestCancellationToken);
        Assert.Equal(5, handled);
    }

    [Fact]
    public async Task Messaging_PublishedEventReachesEveryServiceAndEveryInstance()
    {
        string name = NewName("evt");
        var received = new ConcurrentBag<string>();
        Action<FoundatioMessagingBuilder> Configure(string label) => m => m
            .MapMessage<ConformanceMessage>(name)
            .AddSubscriber<ConformanceMessage>((_, _, _) => { received.Add($"{label}:service"); return Task.CompletedTask; })
            .AddSubscriber<ConformanceMessage>((_, _, _) => { received.Add($"{label}:instance"); return Task.CompletedTask; }, new SubscriptionOptions { Scope = SubscriptionScope.Instance });

        await using var billing1 = await MessagingTestHost.StartAsync(Transport, Log, "billing", Configure("billing1"));
        await using var billing2 = await MessagingTestHost.StartAsync(Transport, Log, "billing", Configure("billing2"));
        await using var shipping = await MessagingTestHost.StartAsync(Transport, Log, "shipping", Configure("shipping"));

        await billing1.Client.PublishAsync(new ConformanceMessage(1), cancellationToken: TestCancellationToken);

        await WaitForAsync(() => received.Count >= 5);
        await Task.Delay(500, TestCancellationToken);
        Assert.Equal(1, received.Count(r => r.StartsWith("billing") && r.EndsWith(":service")));
        Assert.Equal(1, received.Count(r => r == "shipping:service"));
        Assert.Equal(["billing1:instance", "billing2:instance", "shipping:instance"], received.Where(r => r.EndsWith(":instance")).Order());
    }

    [Fact]
    public async Task Messaging_FailingMessageIsRetriedThenDeadLettered()
    {
        string name = NewName("poison");
        int attempts = 0;
        await using var host = await MessagingTestHost.StartAsync(Transport, Log, "worker", m => m
            .MapMessage<ConformanceMessage>(name)
            .AddConsumer<ConformanceMessage>((_, context, _) =>
            {
                Interlocked.Increment(ref attempts);
                throw new InvalidOperationException($"boom {context.Attempt}");
            }, new HandlerOptions { RetryPolicy = new RetryPolicy { MaxAttempts = 3, Delay = RetryPolicy.Fixed(TimeSpan.FromMilliseconds(100)) } }));

        await host.Client.SendAsync(new ConformanceMessage(1), cancellationToken: TestCancellationToken);

        await using var deadLetters = await CreateReceiverAsync(MessageSource.DeadLetter(MessageSource.Queue(name)));
        var deadLetter = await ReceiveOneAsync(deadLetters, DeliveryTimeout * 2);
        Assert.Equal(3, attempts);
        Assert.Contains("boom 3", deadLetter.Headers[KnownHeaders.DeadLetterReason]);
        Assert.Equal(name, deadLetter.Headers[KnownHeaders.MessageType]);
    }

    [Fact]
    public async Task Messaging_DelayedSendIsHandledAfterTheDelay()
    {
        Assert.SkipWhen(Transport.Capabilities.MaxDelay == TimeSpan.Zero, "The transport does not support delays.");
        string name = NewName("later");
        var handledAt = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopwatch = Stopwatch.StartNew();
        await using var host = await MessagingTestHost.StartAsync(Transport, Log, "worker", m => m
            .MapMessage<ConformanceMessage>(name)
            .AddConsumer<ConformanceMessage>((_, _, _) => { handledAt.TrySetResult(stopwatch.Elapsed); return Task.CompletedTask; }));

        await host.Client.SendAsync(new ConformanceMessage(1), new SendOptions { Delay = ShortDelay }, TestCancellationToken);

        var elapsed = await handledAt.Task.WaitAsync(ShortDelay + DeliveryTimeout, TestCancellationToken);
        Assert.True(elapsed >= ShortDelay * 0.75, $"Handled after {elapsed}, expected at least {ShortDelay}.");
    }

    public override async ValueTask DisposeAsync()
    {
        foreach (var transport in _transports)
        {
            try
            {
                await transport.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing transport");
            }
        }

        await base.DisposeAsync();
    }

    protected T Track<T>(T transport) where T : IMessageTransport
    {
        _transports.Add(transport);
        return transport;
    }

    /// <summary>A unique, valid name so concurrent tests never share queues or topics.</summary>
    protected static string NewName(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..(prefix.Length + 13)];

    protected Task<IMessageReceiver> CreateReceiverAsync(MessageSource source, TimeSpan? lease = null)
    {
        return Transport.CreateReceiverAsync(source, new ReceiverOptions { LeaseDuration = lease ?? TimeSpan.FromSeconds(30) }, TestCancellationToken);
    }

    protected static OutgoingMessage Message(string body, string? id = null, IReadOnlyDictionary<string, string>? headers = null)
    {
        return new OutgoingMessage
        {
            MessageId = id ?? Guid.NewGuid().ToString("N"),
            Body = Encoding.UTF8.GetBytes(body),
            Headers = headers ?? new Dictionary<string, string>()
        };
    }

    protected async Task<IncomingMessage> ReceiveOneAsync(IMessageReceiver receiver, TimeSpan? timeout = null)
    {
        return (await ReceiveAsync(receiver, 1, timeout)).Single();
    }

    protected async Task<IReadOnlyList<IncomingMessage>> ReceiveAsync(IMessageReceiver receiver, int count, TimeSpan? timeout = null)
    {
        var messages = new List<IncomingMessage>();
        var deadline = DateTime.UtcNow + (timeout ?? DeliveryTimeout);
        while (messages.Count < count)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException($"Received {messages.Count} of {count} messages from {receiver.Source} within {timeout ?? DeliveryTimeout}.");

            messages.AddRange(await receiver.ReceiveAsync(count - messages.Count, remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1), TestCancellationToken));
        }

        return messages;
    }

    protected async Task AssertNothingReceivedAsync(IMessageReceiver receiver, TimeSpan duration)
    {
        var deadline = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < deadline)
        {
            var remaining = deadline - DateTime.UtcNow;
            var messages = await receiver.ReceiveAsync(10, remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1), TestCancellationToken);
            Assert.True(messages.Count == 0, $"Unexpectedly received message {messages.FirstOrDefault()?.MessageId} from {receiver.Source}.");
        }
    }

    private static async Task AssertLeaseLostOrIgnoredAsync(Func<Task> settle)
    {
        try
        {
            await settle();
        }
        catch (Exception ex) when (ex is not MessageLeaseLostException)
        {
            Assert.Fail($"Settling a stale delivery threw {ex.GetType().Name} instead of MessageLeaseLostException: {ex.Message}");
        }
        catch (MessageLeaseLostException)
        {
        }
    }

    protected async Task WaitForAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DeliveryTimeout);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("The condition was not met in time.");

            await Task.Delay(50, TestCancellationToken);
        }
    }

    public sealed record ConformanceMessage(int Value);
}
