using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;
using Foundatio.Resilience;
using Foundatio.Xunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Foundatio.Tests.Messaging;

/// <summary>
/// The conformance suite every <see cref="IMessageTransport"/> must pass. Derive from it, implement
/// <see cref="CreateTransport"/>, and every test runs against your broker. Tests use unique names so they can run
/// in parallel against a shared broker, and delete the queues and subscriptions they create; tests for capabilities a
/// transport does not declare are skipped. Add tests of your own for what only your broker does.
/// </summary>
/// <example>
/// <code>
/// public class RedisMessageTransportTests(ITestOutputHelper output) : MessageTransportTestsBase(output)
/// {
///     protected override IMessageTransport CreateTransport() => new RedisMessageTransport(...);
/// }
/// </code>
/// </example>
[Experimental(ProviderApi.DiagnosticId, UrlFormat = ProviderApi.UrlFormat)]
public abstract class MessageTransportTestsBase : TestWithLoggingBase
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(60);

    private readonly List<IMessageTransport> _transports = [];
    private readonly ConcurrentDictionary<string, MessageSource> _createdSources = new();
    private IMessageTransport? _transport;
    private IMessageTransport? _peerTransport;

    /// <summary>Creates the suite, logging to <paramref name="output"/>.</summary>
    protected MessageTransportTestsBase(ITestOutputHelper output) : base(output)
    {
    }

    /// <summary>
    /// Creates a transport connected to the broker under test. Called one or more times per test; each transport it
    /// returns must share the broker with the others, as transports in separate processes would.
    /// </summary>
    protected abstract IMessageTransport CreateTransport();

    /// <summary>
    /// Creates a second transport over the same broker as <see cref="Transport"/>, standing in for another process.
    /// Defaults to <see cref="CreateTransport"/>; override it when separate instances of the transport do not share
    /// state, as with an in-process broker.
    /// </summary>
    protected virtual IMessageTransport CreatePeerTransport() => CreateTransport();

    /// <summary>The longest a test waits for a message the broker should deliver.</summary>
    protected virtual TimeSpan DeliveryTimeout => TimeSpan.FromSeconds(10);

    /// <summary>The lease used by tests that wait for leases to expire.</summary>
    protected virtual TimeSpan ShortLease => TimeSpan.FromSeconds(2);

    /// <summary>The delay used by delayed-delivery tests.</summary>
    protected virtual TimeSpan ShortDelay => TimeSpan.FromSeconds(2);

    /// <summary>The longest a test waits for <see cref="IMessageTransport.GetStatsAsync"/> to reflect sends and settlements, which it may lag.</summary>
    protected virtual TimeSpan StatsTimeout => TimeSpan.FromSeconds(15);

    /// <summary>The transport under test, created by <see cref="CreateTransport"/> on first use and disposed after the test.</summary>
    protected IMessageTransport Transport => _transport ??= Track(CreateTransport());

    /// <summary>A transport from <see cref="CreatePeerTransport"/>: another process connected to the same broker.</summary>
    protected IMessageTransport PeerTransport => _peerTransport ??= Track(CreatePeerTransport());

    /// <summary>A sent message is received with the same id, body, headers and group key, and a delivery count of 1.</summary>
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

    /// <summary>Ids and group keys of the longest allowed length, with spaces, punctuation and non-ASCII characters, are received unchanged.</summary>
    [Fact]
    public async Task SendThenReceive_IdsAndGroupKeyWithAnyCharacters_RoundTrip()
    {
        string id = PadToMaxIdLength("id with spaces, \"quotes\", slashes/colons: ünïcödé ✓ 😀 ", 'i');
        string groupKey = PadToMaxIdLength("group with spaces ✓ 😀 ", 'g');
        string? deduplicationId = Transport.Capabilities.Deduplication ? PadToMaxIdLength("dedup id ✓ 😀 ", 'd') : null;
        var source = MessageSource.Queue(NewName("odd-ids"));
        await using var receiver = await CreateReceiverAsync(source);

        await Transport.SendAsync(MessageDestination.Queue(source.Name),
            [Message("odd ids", id) with { GroupKey = groupKey, DeduplicationId = deduplicationId }], TestCancellationToken);

        var message = await ReceiveOneAsync(receiver);
        Assert.Equal(id, message.MessageId);
        Assert.Equal(groupKey, message.GroupKey);
        Assert.Equal("odd ids", Encoding.UTF8.GetString(message.Body.Span));
        await receiver.CompleteAsync(message, TestCancellationToken);
    }

    /// <summary>A receive from an empty source returns an empty list once its maximum wait has passed.</summary>
    [Fact]
    public async Task Receive_WhenEmpty_ReturnsEmptyWithinMaxWait()
    {
        await using var receiver = await CreateReceiverAsync(MessageSource.Queue(NewName("empty")));
        var stopwatch = Stopwatch.StartNew();

        var messages = await receiver.ReceiveAsync(10, TimeSpan.FromSeconds(1), TestCancellationToken);

        Assert.Empty(messages);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1) + TimeSpan.FromSeconds(4), $"ReceiveAsync took {stopwatch.Elapsed} with a 1s maxWait.");
    }

    /// <summary>A cancelled receive throws promptly, and a message sent afterwards is still received.</summary>
    [Fact]
    public async Task Receive_WhenCancelled_ThrowsPromptlyAndLosesNothing()
    {
        var source = MessageSource.Queue(NewName("cancel"));
        await using var receiver = await CreateReceiverAsync(source, ShortLease);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestCancellationToken);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(500));
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => receiver.ReceiveAsync(10, TimeSpan.FromSeconds(30), cancellation.Token));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"ReceiveAsync took {stopwatch.Elapsed} to observe a cancellation after 500ms.");
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("after cancel", "id-after-cancel")], TestCancellationToken);
        var message = await ReceiveOneAsync(receiver, ShortLease + DeliveryTimeout);
        Assert.Equal("id-after-cancel", message.MessageId);
        await receiver.CompleteAsync(message, TestCancellationToken);
    }

    /// <summary>A receive never returns more messages than it asked for.</summary>
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

    /// <summary>A message sent to a queue before any receiver exists is kept until one opens.</summary>
    [Fact]
    public async Task Send_BeforeAnyReceiverExists_KeepsTheMessage()
    {
        var source = MessageSource.Queue(NewName("early"));
        DeleteAfterTest(source);
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("early")], TestCancellationToken);

        await using var receiver = await CreateReceiverAsync(source);
        var message = await ReceiveOneAsync(receiver);
        Assert.Equal("early", Encoding.UTF8.GetString(message.Body.Span));
    }

    /// <summary>A completed message is not delivered again.</summary>
    [Fact]
    public async Task Complete_RemovesTheMessage()
    {
        var source = MessageSource.Queue(NewName("complete"));
        await using var receiver = await CreateReceiverAsync(source, ShortLease);
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("done")], TestCancellationToken);

        await receiver.CompleteAsync(await ReceiveOneAsync(receiver), TestCancellationToken);

        await AssertNothingReceivedAsync(receiver, ShortLease + TimeSpan.FromSeconds(1));
    }

    /// <summary>An abandoned message is delivered again with a higher delivery count.</summary>
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

    /// <summary>A message abandoned while another receive is waiting reaches that receive.</summary>
    [Fact]
    public async Task Abandon_WhileAReceiveIsWaiting_RedeliversTheMessage()
    {
        var source = MessageSource.Queue(NewName("settle-race"));
        await using var receiver = await CreateReceiverAsync(source);
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("raced", "id-raced")], TestCancellationToken);
        var first = await ReceiveOneAsync(receiver);

        var waiting = receiver.ReceiveAsync(10, DeliveryTimeout, TestCancellationToken);
        await receiver.RenewAsync(first, TestCancellationToken);
        await receiver.AbandonAsync(first, TimeSpan.Zero, TestCancellationToken);

        var messages = (await waiting).ToList();
        if (messages.Count == 0)
            messages.AddRange(await ReceiveAsync(receiver, 1));

        var again = Assert.Single(messages);
        Assert.Equal("id-raced", again.MessageId);
        Assert.True(again.DeliveryCount > first.DeliveryCount, $"DeliveryCount stayed at {again.DeliveryCount} after the abandon.");
        await receiver.CompleteAsync(again, TestCancellationToken);
    }

    /// <summary>A message abandoned with a delay is not delivered again before the delay has passed.</summary>
    [Fact]
    public async Task Abandon_WithDelay_WaitsBeforeRedelivering()
    {
        Assert.SkipWhen(Transport.Capabilities.MaxRetryDelay == TimeSpan.Zero, "The transport does not support retry delays.");
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

    /// <summary>A message that is not settled is delivered again once its lease runs out.</summary>
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

    /// <summary>Renewing keeps a message from being delivered again past its original lease.</summary>
    [Fact]
    public async Task Renew_ExtendsTheLease()
    {
        Assert.SkipUnless(Transport.Capabilities.LeaseExpiration, "The transport does not expire leases.");
        var source = MessageSource.Queue(NewName("renew"));
        await using var receiver = await CreateReceiverAsync(source, ShortLease);
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("renewed")], TestCancellationToken);

        var message = await ReceiveOneAsync(receiver);
        for (int i = 0; i < 4; i++)
        {
            await AssertNothingReceivedAsync(receiver, ShortLease / 2);
            await receiver.RenewAsync(message, TestCancellationToken);
        }

        await receiver.CompleteAsync(message, TestCancellationToken);
    }

    /// <summary>A delivery that still holds its lease can be renewed many times in a row.</summary>
    [Fact]
    public async Task Renew_OfAHeldDelivery_SucceedsRepeatedly()
    {
        var source = MessageSource.Queue(NewName("renew-held"));
        await using var receiver = await CreateReceiverAsync(source);
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("held")], TestCancellationToken);

        var message = await ReceiveOneAsync(receiver);
        for (int i = 0; i < 3; i++)
            await receiver.RenewAsync(message, TestCancellationToken);

        await receiver.CompleteAsync(message, TestCancellationToken);
        await AssertNothingReceivedAsync(receiver, ShortDelay);
    }

    /// <summary>A renewal after the lease ran out either takes the lease back, keeping the message with this delivery, or throws <see cref="MessageLeaseLostException"/> and the message is delivered again.</summary>
    [Fact]
    public async Task Renew_AfterTheLeaseExpired_TakesItBackOrThrowsLeaseLost()
    {
        Assert.SkipUnless(Transport.Capabilities.LeaseExpiration, "The transport does not expire leases.");
        var source = MessageSource.Queue(NewName("renew-late"));
        await using var receiver = await CreateReceiverAsync(source, ShortLease);
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("late", "id-late")], TestCancellationToken);
        var message = await ReceiveOneAsync(receiver);
        await Task.Delay(ShortLease + TimeSpan.FromSeconds(1), TestCancellationToken);

        try
        {
            await receiver.RenewAsync(message, TestCancellationToken);
        }
        catch (MessageLeaseLostException)
        {
            var again = await ReceiveOneAsync(receiver, ShortLease + DeliveryTimeout);
            Assert.Equal("id-late", again.MessageId);
            Assert.True(again.DeliveryCount > message.DeliveryCount, $"DeliveryCount stayed at {again.DeliveryCount} after the lease was lost.");
            await receiver.CompleteAsync(again, TestCancellationToken);
            return;
        }

        // The renew took the lease back, so the message must stay with this delivery.
        await AssertNothingReceivedAsync(receiver, ShortLease * 0.75);
        await receiver.CompleteAsync(message, TestCancellationToken);
    }

    /// <summary>Messages a disposed receiver did not settle are delivered again, with a higher delivery count.</summary>
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

    /// <summary>Disposing a transport makes its receivers' unsettled messages available to another process.</summary>
    [Fact]
    public async Task DisposingATransport_ReleasesItsUnsettledMessagesToAnotherProcess()
    {
        var source = MessageSource.Queue(NewName("process-stop"));
        var stopped = CreatePeerTransport();
        try
        {
            var receiver = await stopped.CreateReceiverAsync(source, new ReceiverOptions { LeaseDuration = TimeSpan.FromMinutes(5) }, TestCancellationToken);
            DeleteAfterTest(source);
            await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("orphan", "id-orphan")], TestCancellationToken);
            var held = await ReceiveOneAsync(receiver);

            await stopped.DisposeAsync();

            await using var survivor = await CreateReceiverAsync(source);
            var message = await ReceiveOneAsync(survivor);
            Assert.Equal("id-orphan", message.MessageId);
            Assert.Equal("orphan", Encoding.UTF8.GetString(message.Body.Span));
            await AssertLeaseLostAsync(() => receiver.RenewAsync(held, TestCancellationToken), receiverDisposed: true);
            await survivor.CompleteAsync(message, TestCancellationToken);
        }
        finally
        {
            await stopped.DisposeAsync();
        }
    }

    /// <summary>Settling a delivery whose lease moved to a newer delivery throws <see cref="MessageLeaseLostException"/> and leaves the newer one alone.</summary>
    [Fact]
    public async Task StaleSettlement_FailsWithLeaseLostAndLeavesTheNewDeliveryIntact()
    {
        Assert.SkipUnless(Transport.Capabilities.LeaseExpiration, "The transport does not expire leases.");
        var source = MessageSource.Queue(NewName("stale"));
        await using var first = await CreateReceiverAsync(source, ShortLease);
        await using var second = await CreateReceiverAsync(source);
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("contested", "id-contested")], TestCancellationToken);

        var stale = await ReceiveOneAsync(first);
        var current = await ReceiveOneAsync(second, ShortLease + DeliveryTimeout);
        Assert.Equal(stale.MessageId, current.MessageId);

        await AssertStaleSettlementLeavesTheNewDeliveryIntactAsync(source, first, stale, second, current, receiverDisposed: false);
    }

    /// <summary>Settling through a disposed receiver throws and leaves the message's newer delivery alone.</summary>
    [Fact]
    public async Task StaleSettlement_AfterTheReceiverIsDisposed_FailsAndLeavesTheNewDeliveryIntact()
    {
        var source = MessageSource.Queue(NewName("stale-closed"));
        await using var first = await CreateReceiverAsync(source);
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("contested", "id-contested")], TestCancellationToken);
        var stale = await ReceiveOneAsync(first);
        await first.DisposeAsync();

        await using var second = await CreateReceiverAsync(source);
        var current = await ReceiveOneAsync(second);

        Assert.Equal(stale.MessageId, current.MessageId);
        Assert.True(current.DeliveryCount > stale.DeliveryCount, $"DeliveryCount stayed at {current.DeliveryCount} after the receiver was disposed.");
        await AssertStaleSettlementLeavesTheNewDeliveryIntactAsync(source, first, stale, second, current, receiverDisposed: true);
    }

    /// <summary>A dead-lettered message leaves its queue and is received from its dead-letter queue with its id, body, headers and reason.</summary>
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

    /// <summary>A message dead-lettered from a subscription goes to that subscription's dead-letter queue and no other.</summary>
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

    /// <summary>A message at the size limit can be dead-lettered with a long reason without being lost.</summary>
    [Fact]
    public async Task DeadLetter_OfAMessageAtTheSizeLimitWithALongReason_KeepsTheMessage()
    {
        var headers = new Dictionary<string, string> { [KnownHeaders.MessageType] = "conformance.size" };
        long limit = Transport.Capabilities.MaxMessageSize ?? 64 * 1024;
        var body = new byte[(int)(limit - MessageSize.Of("dead-at-limit", ReadOnlySpan<byte>.Empty, headers))];
        Random.Shared.NextBytes(body);
        string reason = new('r', 4096);
        var source = MessageSource.Queue(NewName("dlq-size"));
        await using var receiver = await CreateReceiverAsync(source);
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [new OutgoingMessage { MessageId = "dead-at-limit", Body = body, Headers = headers }], TestCancellationToken);

        await receiver.DeadLetterAsync(await ReceiveOneAsync(receiver), reason, TestCancellationToken);

        await using var deadLetters = await CreateReceiverAsync(MessageSource.DeadLetter(source));
        var deadLetter = await ReceiveOneAsync(deadLetters);
        Assert.True(deadLetter.Body.Span.SequenceEqual(body), "The dead letter's body changed.");
        Assert.Equal("conformance.size", deadLetter.Headers[KnownHeaders.MessageType]);
        string kept = deadLetter.Headers[KnownHeaders.DeadLetterReason];
        Assert.StartsWith(kept, reason);
        Assert.True(kept.Length >= 1024, $"The dead-letter reason was cut to {kept.Length} characters; at least the first 1,024 must be kept.");
    }

    /// <summary>Dead-lettering two messages with the same id keeps both dead letters, each with its reason.</summary>
    [Fact]
    public async Task DeadLetter_OfTheSameMessageAgain_KeepsBothDeadLetters()
    {
        var source = MessageSource.Queue(NewName("dlq-twice"));
        await using var receiver = await CreateReceiverAsync(source);

        for (int i = 0; i < 2; i++)
        {
            await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("poison", "id-poison")], TestCancellationToken);
            await receiver.DeadLetterAsync(await ReceiveOneAsync(receiver), $"attempt {i}", TestCancellationToken);
        }

        await using var deadLetters = await CreateReceiverAsync(MessageSource.DeadLetter(source));
        var messages = await ReceiveAsync(deadLetters, 2);
        Assert.Equal(["attempt 0", "attempt 1"], messages.Select(m => m.Headers[KnownHeaders.DeadLetterReason]).Order());
        Assert.All(messages, m => Assert.Equal("id-poison", m.MessageId));
    }

    /// <summary>A message sent with a delivery time is not received before it.</summary>
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

    /// <summary>A message published with a delivery time reaches subscriptions no earlier than it.</summary>
    [Fact]
    public async Task DeliverAt_DelaysPublishedMessages()
    {
        Assert.SkipWhen(Transport.Capabilities.MaxPublishDelay == TimeSpan.Zero, "The transport does not support publish delays.");
        string topic = NewName("publish-delay");
        await using var receiver = await CreateReceiverAsync(MessageSource.Subscription(topic, "delayed"));
        var stopwatch = Stopwatch.StartNew();

        await Transport.SendAsync(MessageDestination.Topic(topic), [Message("later event") with { DeliverAt = DateTimeOffset.UtcNow.Add(ShortDelay) }], TestCancellationToken);

        var message = await ReceiveOneAsync(receiver, ShortDelay + DeliveryTimeout);
        Assert.True(stopwatch.Elapsed >= ShortDelay * 0.75, $"Delivered after {stopwatch.Elapsed}, expected at least {ShortDelay}.");
        Assert.Equal(1, message.DeliveryCount);
        await receiver.CompleteAsync(message, TestCancellationToken);
    }

    /// <summary>Receivers sharing a queue each get different messages, and every message is delivered.</summary>
    [Fact]
    public async Task CompetingReceivers_EachMessageIsDeliveredOnce()
    {
        var source = MessageSource.Queue(NewName("compete"));
        await using var first = await CreateReceiverAsync(source);
        await using var second = await CreatePeerReceiverAsync(source);
        await Transport.SendAsync(MessageDestination.Queue(source.Name), Enumerable.Range(0, 20).Select(i => Message($"m{i}", $"id-{i}")).ToList(), TestCancellationToken);

        var received = new ConcurrentBag<string>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestCancellationToken);
        timeout.CancelAfter(DeliveryTimeout * 2);

        async Task DrainAsync(IMessageReceiver receiver)
        {
            while (received.Count < 20 && !timeout.IsCancellationRequested)
            {
                foreach (var message in await receiver.ReceiveAsync(5, TimeSpan.FromSeconds(1), TestCancellationToken))
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

    /// <summary>A published message reaches every subscription of the topic.</summary>
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

    /// <summary>A publish reaches the durable and instance subscriptions another process opened after the topic was first used.</summary>
    [Fact]
    public async Task Publish_AfterAnotherProcessSubscribes_ReachesItsNewSubscriptions()
    {
        string topic = NewName("late-process");
        await Transport.SendAsync(MessageDestination.Topic(topic), [Message("before", "id-before")], TestCancellationToken);

        await using var durable = await CreatePeerReceiverAsync(MessageSource.Subscription(topic, "late"));
        await using var instance = await CreatePeerReceiverAsync(MessageSource.InstanceSubscription(topic, "late"));
        await Transport.SendAsync(MessageDestination.Topic(topic), [Message("after", "id-after")], TestCancellationToken);

        foreach (var receiver in new[] { durable, instance })
        {
            var message = await ReceiveOneAsync(receiver);
            Assert.Equal("id-after", message.MessageId);
            await receiver.CompleteAsync(message, TestCancellationToken);
        }
    }

    /// <summary>A durable subscription keeps the messages published while none of its receivers is open.</summary>
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

    /// <summary>Receivers of one durable subscription share its messages, each delivered to one of them.</summary>
    [Fact]
    public async Task Subscription_CompetingReceiversShareMessages()
    {
        string topic = NewName("sub-compete");
        var source = MessageSource.Subscription(topic, "workers");
        await using var first = await CreateReceiverAsync(source);
        await using var second = await CreatePeerReceiverAsync(source);
        await Transport.SendAsync(MessageDestination.Topic(topic), Enumerable.Range(0, 10).Select(i => Message($"e{i}", $"id-{i}")).ToList(), TestCancellationToken);

        var received = new ConcurrentBag<string>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestCancellationToken);
        timeout.CancelAfter(DeliveryTimeout * 2);

        async Task DrainAsync(IMessageReceiver receiver)
        {
            while (received.Count < 10 && !timeout.IsCancellationRequested)
            {
                foreach (var message in await receiver.ReceiveAsync(3, TimeSpan.FromSeconds(1), TestCancellationToken))
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

    /// <summary>Every open instance subscription gets a copy of each published message; a closed one stops getting copies, and a new one gets only what is published after it opens.</summary>
    [Fact]
    public async Task InstanceSubscriptions_EachGetACopyOnlyWhileOpen()
    {
        string topic = NewName("instances");
        var first = await CreateReceiverAsync(MessageSource.InstanceSubscription(topic, "conformance"));
        await using var second = await CreatePeerReceiverAsync(MessageSource.InstanceSubscription(topic, "conformance"));

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
        await Transport.SendAsync(MessageDestination.Topic(topic), [Message("to the late one", "id-late")], TestCancellationToken);
        await AssertOnlyReceivedAsync(late, "id-late");
    }

    /// <summary>Publishing to a topic without subscriptions succeeds.</summary>
    [Fact]
    public Task Publish_WithoutSubscriptions_Succeeds()
    {
        return Transport.SendAsync(MessageDestination.Topic(NewName("nobody")), [Message("unheard")], TestCancellationToken);
    }

    /// <summary>A message sent to one subscription reaches it and none of the topic's other subscriptions.</summary>
    [Fact]
    public async Task SendToSubscription_DeliversOnlyToThatSubscription()
    {
        string topic = NewName("direct");
        await using var target = await CreateReceiverAsync(MessageSource.Subscription(topic, "target"));
        await using var other = await CreateReceiverAsync(MessageSource.Subscription(topic, "other"));
        await using var instance = await CreateReceiverAsync(MessageSource.InstanceSubscription(topic, "conformance"));

        await Transport.SendAsync(MessageDestination.Subscription(topic, "target"), [Message("just you", "id-direct") with { GroupKey = "g" }], TestCancellationToken);

        var message = await ReceiveOneAsync(target);
        Assert.Equal("id-direct", message.MessageId);
        Assert.Equal("just you", Encoding.UTF8.GetString(message.Body.Span));
        Assert.Equal("g", message.GroupKey);
        await target.CompleteAsync(message, TestCancellationToken);

        await Transport.SendAsync(MessageDestination.Topic(topic), [Message("to everyone", "id-everyone")], TestCancellationToken);
        await AssertOnlyReceivedAsync(other, "id-everyone");
        await AssertOnlyReceivedAsync(instance, "id-everyone");
    }

    /// <summary>A message sent to a subscription before any receiver exists is kept until one opens.</summary>
    [Fact]
    public async Task SendToSubscription_BeforeAnyReceiverExists_KeepsTheMessage()
    {
        var source = MessageSource.Subscription(NewName("direct-new"), "late");
        DeleteAfterTest(source);
        await Transport.SendAsync(MessageDestination.For(source), [Message("kept", "id-kept")], TestCancellationToken);

        await using var receiver = await CreateReceiverAsync(source);
        var message = await ReceiveOneAsync(receiver);
        Assert.Equal("id-kept", message.MessageId);
        await receiver.CompleteAsync(message, TestCancellationToken);
    }

    /// <summary>Deleting a queue removes its pending messages and its dead letters.</summary>
    [Fact]
    public async Task Delete_Queue_RemovesItsMessagesAndDeadLetters()
    {
        var source = MessageSource.Queue(NewName("delete"));
        await using (var receiver = await CreateReceiverAsync(source))
        {
            await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("poison", "id-poison"), Message("pending", "id-pending")], TestCancellationToken);
            await receiver.DeadLetterAsync(await ReceiveOneAsync(receiver), "conformance", TestCancellationToken);
        }

        await Transport.DeleteAsync(source, TestCancellationToken);

        await using var recreated = await CreateReceiverAsync(source);
        await using var deadLetters = await CreateReceiverAsync(MessageSource.DeadLetter(source));
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("after", "id-after")], TestCancellationToken);
        var after = Assert.Single(await AssertOnlyReceivedAsync(recreated, "id-after", complete: false));
        await recreated.DeadLetterAsync(after, "conformance", TestCancellationToken);
        await AssertOnlyReceivedAsync(deadLetters, "id-after");
    }

    /// <summary>A deleted subscription loses its pending copies and gets none of later publishes until it is received from again.</summary>
    [Fact]
    public async Task Delete_Subscription_StopsCopiesUntilItIsReceivedAgain()
    {
        string topic = NewName("delete-sub");
        var source = MessageSource.Subscription(topic, "removed");
        await (await CreateReceiverAsync(source)).DisposeAsync();
        await Transport.SendAsync(MessageDestination.Topic(topic), [Message("before delete", "id-before")], TestCancellationToken);

        await Transport.DeleteAsync(source, TestCancellationToken);
        await Transport.SendAsync(MessageDestination.Topic(topic), [Message("while deleted", "id-while")], TestCancellationToken);

        await using var receiver = await CreateReceiverAsync(source);
        await Transport.SendAsync(MessageDestination.Topic(topic), [Message("after", "id-after")], TestCancellationToken);
        await AssertOnlyReceivedAsync(receiver, "id-after");
    }

    /// <summary>Deleting a dead-letter source removes the dead letters and keeps the source's pending messages.</summary>
    [Fact]
    public async Task Delete_DeadLetterSource_RemovesOnlyTheDeadLetters()
    {
        var source = MessageSource.Queue(NewName("purge"));
        await using var receiver = await CreateReceiverAsync(source);
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("poison", "id-poison")], TestCancellationToken);
        await receiver.DeadLetterAsync(await ReceiveOneAsync(receiver), "conformance", TestCancellationToken);
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("good", "id-good")], TestCancellationToken);

        await Transport.DeleteAsync(MessageSource.DeadLetter(source), TestCancellationToken);

        await using var deadLetters = await CreateReceiverAsync(MessageSource.DeadLetter(source));
        var good = await ReceiveOneAsync(receiver);
        Assert.Equal("id-good", good.MessageId);
        await receiver.DeadLetterAsync(good, "conformance", TestCancellationToken);
        await AssertOnlyReceivedAsync(deadLetters, "id-good");
    }

    /// <summary>Deleting a source with an open receiver either throws, or leaves the receiver receiving what is sent afterwards, or makes it throw; it never silently stops delivering.</summary>
    [Fact]
    public async Task Delete_WhileAReceiverIsOpen_RefusesOrTheReceiverKeepsReceivingOrFaults()
    {
        var source = MessageSource.Queue(NewName("delete-open"));
        await using var receiver = await CreatePeerReceiverAsync(source);
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("before", "id-before")], TestCancellationToken);
        await receiver.CompleteAsync(await ReceiveOneAsync(receiver), TestCancellationToken);

        bool refused = false;
        try
        {
            await Transport.DeleteAsync(source, TestCancellationToken);
        }
        catch (MessageBusException ex)
        {
            _logger.LogInformation(ex, "The transport refused to delete a source with an open receiver");
            refused = true;
        }

        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("after", "id-after")], TestCancellationToken);
        if (refused)
        {
            await AssertOnlyReceivedAsync(receiver, "id-after");
            return;
        }

        IncomingMessage message;
        try
        {
            message = await ReceiveOneAsync(receiver);
        }
        catch (Exception ex) when (ex is not TimeoutException and not OperationCanceledException)
        {
            _logger.LogInformation(ex, "The receiver faulted after its source was deleted; replacing it");
            await using var replacement = await CreateReceiverAsync(source);
            message = await ReceiveOneAsync(replacement);
            await replacement.CompleteAsync(message, TestCancellationToken);
            Assert.Equal("id-after", message.MessageId);
            return;
        }

        Assert.Equal("id-after", message.MessageId);
        await receiver.CompleteAsync(message, TestCancellationToken);
    }

    /// <summary>Deleting a source that does not exist succeeds.</summary>
    [Fact]
    public async Task Delete_WhenTheSourceDoesNotExist_Succeeds()
    {
        var queue = MessageSource.Queue(NewName("never"));
        await Transport.DeleteAsync(queue, TestCancellationToken);
        await Transport.DeleteAsync(queue, TestCancellationToken);
        await Transport.DeleteAsync(MessageSource.Subscription(NewName("never-topic"), "never"), TestCancellationToken);
        await Transport.DeleteAsync(MessageSource.DeadLetter(queue), TestCancellationToken);
        await Assert.ThrowsAsync<ArgumentException>(() => Transport.DeleteAsync(MessageSource.InstanceSubscription(NewName("instances")), TestCancellationToken));
    }

    /// <summary>A sent message of exactly <see cref="TransportCapabilities.MaxMessageSize"/>, as <see cref="MessageSize"/> measures it, is received unchanged.</summary>
    [Fact]
    public async Task MaxMessageSize_MessageAtTheLimit_RoundTrips()
    {
        Assert.SkipUnless(Transport.Capabilities.MaxMessageSize is not null, "The transport does not declare a message size limit.");
        var headers = ManyHeaders();
        var body = new byte[(int)(Transport.Capabilities.MaxMessageSize!.Value - MessageSize.Of("at-limit", ReadOnlySpan<byte>.Empty, headers))];
        Random.Shared.NextBytes(body);
        var source = MessageSource.Queue(NewName("size"));
        await using var receiver = await CreateReceiverAsync(source);

        await Transport.SendAsync(MessageDestination.Queue(source.Name), [new OutgoingMessage { MessageId = "at-limit", Body = body, Headers = headers }], TestCancellationToken);

        var message = await ReceiveOneAsync(receiver);
        Assert.True(message.Body.Span.SequenceEqual(body), "The body at the size limit did not round-trip.");
        Assert.Equal(headers, message.Headers);
    }

    /// <summary>A published message of exactly <see cref="TransportCapabilities.MaxMessageSize"/> is received unchanged.</summary>
    [Fact]
    public async Task MaxMessageSize_PublishedMessageAtTheLimit_RoundTrips()
    {
        Assert.SkipUnless(Transport.Capabilities.MaxMessageSize is not null, "The transport does not declare a message size limit.");
        var headers = ManyHeaders();
        var body = new byte[(int)(Transport.Capabilities.MaxMessageSize!.Value - MessageSize.Of("at-limit", ReadOnlySpan<byte>.Empty, headers))];
        Random.Shared.NextBytes(body);
        string topic = NewName("size-topic");
        await using var receiver = await CreateReceiverAsync(MessageSource.Subscription(topic, "large"));

        await Transport.SendAsync(MessageDestination.Topic(topic), [new OutgoingMessage { MessageId = "at-limit", Body = body, Headers = headers }], TestCancellationToken);

        var message = await ReceiveOneAsync(receiver);
        Assert.True(message.Body.Span.SequenceEqual(body), "The published body at the size limit did not round-trip.");
        Assert.Equal(headers, message.Headers);
    }

    /// <summary>A message one byte over <see cref="TransportCapabilities.MaxMessageSize"/> throws <see cref="MessageTooLargeException"/>.</summary>
    [Fact]
    public Task MaxMessageSize_LargerMessage_ThrowsMessageTooLarge()
    {
        Assert.SkipUnless(Transport.Capabilities.MaxMessageSize is not null, "The transport does not declare a message size limit.");
        var body = new byte[(int)Transport.Capabilities.MaxMessageSize!.Value + 1];
        var source = MessageSource.Queue(NewName("too-big"));
        DeleteAfterTest(source);

        return Assert.ThrowsAsync<MessageTooLargeException>(() => Transport.SendAsync(MessageDestination.Queue(source.Name),
            [new OutgoingMessage { MessageId = "too-big", Body = body }], TestCancellationToken));
    }

    /// <summary>A batch of <see cref="TransportCapabilities.MaxSendBatchSize"/> messages is delivered in full.</summary>
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

    /// <summary>A batch of large messages is delivered in full.</summary>
    [Fact]
    public async Task SendBatch_OfLargeMessages_DeliversEveryMessage()
    {
        int count = Math.Min(Transport.Capabilities.MaxSendBatchSize, 10);
        int size = (int)Math.Min(Transport.Capabilities.MaxMessageSize / 2 - 64 ?? 128 * 1024, 128 * 1024);
        var source = MessageSource.Queue(NewName("large-batch"));
        await using var receiver = await CreateReceiverAsync(source);
        var messages = Enumerable.Range(0, count).Select(i =>
        {
            var body = new byte[size];
            Random.Shared.NextBytes(body);
            return new OutgoingMessage { MessageId = $"id-{i}", Body = body };
        }).ToList();

        await Transport.SendAsync(MessageDestination.Queue(source.Name), messages, TestCancellationToken);

        var received = (await ReceiveAsync(receiver, count)).ToDictionary(m => m.MessageId);
        Assert.Equal(messages.Select(m => m.MessageId).Order(), received.Keys.Order());
        Assert.All(messages, m => Assert.True(received[m.MessageId].Body.Span.SequenceEqual(m.Body.Span), $"The body of {m.MessageId} changed."));
    }

    /// <summary>Queue, topic and subscription names with upper and lower case letters, digits, '.', '_' and '-' work.</summary>
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

    /// <summary>Empty bodies and large binary bodies are received unchanged.</summary>
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

    /// <summary>Messages of a group are delivered in order, and none is delivered while an earlier one of its group is unsettled.</summary>
    [Fact]
    public async Task OrderedGroups_AreDeliveredInOrderAndHeldWhileEarlierOnesAreUnsettled()
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
            // Several messages of a group may arrive together, in order.
            var batch = await receiver.ReceiveAsync(10, TimeSpan.FromSeconds(1), TestCancellationToken);
            if (batch.Any(m => m.GroupKey == "a"))
            {
                // While any of them is unsettled, no later message of the group may be delivered, even as messages
                // sent after it are.
                string canary = $"canary-{order.Count}";
                await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("canary", canary) with { GroupKey = canary }], TestCancellationToken);
                var meanwhile = await ReceiveUntilAsync(receiver, canary);
                Assert.DoesNotContain(meanwhile, m => m.GroupKey == "a");
                batch = [.. batch, .. meanwhile];
            }

            foreach (var message in batch)
            {
                order.Add(message.MessageId);
                await receiver.CompleteAsync(message, TestCancellationToken);
            }
        }

        Assert.Equal(["a-0", "a-1", "a-2", "a-3"], order.Where(id => id.StartsWith("a-")));
        Assert.Contains("b-0", order);
    }

    /// <summary>Receivers in different processes never handle two messages of one group at the same time.</summary>
    [Fact]
    public async Task OrderedGroups_WithCompetingReceiversInSeparateProcesses_HoldTheGroupUntilItsEarlierMessageIsSettled()
    {
        Assert.SkipUnless(Transport.Capabilities.OrderedGroups, "The transport does not order groups.");
        var source = MessageSource.Queue(NewName("groups-compete"));
        await using var first = await CreateReceiverAsync(source);
        await using var second = await CreatePeerReceiverAsync(source);
        IMessageReceiver[] receivers = [first, second];
        await Transport.SendAsync(MessageDestination.Queue(source.Name),
            Enumerable.Range(0, 3).Select(i => Message($"a{i}", $"a-{i}") with { GroupKey = "a" }).ToList(), TestCancellationToken);

        var (holder, held) = Assert.Single(await ReceiveFromAnyAsync(receivers, 1, DeliveryTimeout));
        Assert.Equal("a-0", held.MessageId);

        // While a-0 is unsettled, no receiver may get a later message of its group, even as messages sent after it arrive.
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("b0", "b-0") with { GroupKey = "b" }], TestCancellationToken);
        var meanwhile = await ReceiveFromAnyUntilAsync(receivers, "b-0", DeliveryTimeout);
        Assert.DoesNotContain(meanwhile, d => d.Message.GroupKey == "a");
        foreach (var (receiver, message) in meanwhile)
            await receiver.CompleteAsync(message, TestCancellationToken);

        await holder.CompleteAsync(held, TestCancellationToken);
        var order = await DrainGroupInOrderAsync(receivers, "a", 2);
        Assert.Equal(["a-1", "a-2"], order);
    }

    /// <summary>A message abandoned with a delay holds back the rest of its group, on receivers in every process, until it is delivered again.</summary>
    [Fact]
    public async Task OrderedGroups_WithCompetingReceivers_ARetriedMessageHoldsBackItsGroupUntilItIsRedelivered()
    {
        Assert.SkipUnless(Transport.Capabilities.OrderedGroups, "The transport does not order groups.");
        Assert.SkipWhen(Transport.Capabilities.MaxRetryDelay == TimeSpan.Zero, "The transport does not support retry delays.");
        var source = MessageSource.Queue(NewName("group-retry-compete"));
        await using var first = await CreateReceiverAsync(source);
        await using var second = await CreatePeerReceiverAsync(source);
        IMessageReceiver[] receivers = [first, second];
        await Transport.SendAsync(MessageDestination.Queue(source.Name),
            Enumerable.Range(0, 2).Select(i => Message($"a{i}", $"a-{i}") with { GroupKey = "a" }).ToList(), TestCancellationToken);

        var (holder, held) = Assert.Single(await ReceiveFromAnyAsync(receivers, 1, DeliveryTimeout));
        Assert.Equal("a-0", held.MessageId);
        var stopwatch = Stopwatch.StartNew();
        await holder.AbandonAsync(held, ShortDelay, TestCancellationToken);

        var deliveries = await ReceiveFromAnyUntilAsync(receivers, "a-0", ShortDelay + DeliveryTimeout);
        var elapsed = stopwatch.Elapsed;
        int retriedAt = deliveries.FindIndex(d => d.Message.MessageId == "a-0");
        var (retriedBy, retried) = deliveries[retriedAt];
        Assert.True(elapsed >= ShortDelay * 0.75, $"Redelivered after {elapsed}, expected at least {ShortDelay}.");
        Assert.True(retried.DeliveryCount > held.DeliveryCount, $"DeliveryCount stayed at {retried.DeliveryCount} after the retry.");
        Assert.All(deliveries.Where(d => d.Message.MessageId == "a-1"), d =>
        {
            Assert.Same(retriedBy, d.Receiver);
            Assert.True(deliveries.IndexOf(d) > retriedAt, "a-1 was delivered while a-0 was waiting to be retried.");
        });

        var order = new List<string>();
        foreach (var (receiver, message) in deliveries)
        {
            order.Add(message.MessageId);
            await receiver.CompleteAsync(message, TestCancellationToken);
        }

        if (!order.Contains("a-1"))
            order.AddRange(await DrainGroupInOrderAsync(receivers, "a", 1));

        Assert.Equal(["a-0", "a-1"], order);
    }

    /// <summary>An abandoned message of a group is delivered again before the group's later messages.</summary>
    [Fact]
    public async Task OrderedGroups_AnAbandonedMessageIsRedeliveredBeforeLaterOnes()
    {
        Assert.SkipUnless(Transport.Capabilities.OrderedGroups, "The transport does not order groups.");
        var source = MessageSource.Queue(NewName("group-retry"));
        await using var receiver = await CreateReceiverAsync(source);
        await Transport.SendAsync(MessageDestination.Queue(source.Name),
            Enumerable.Range(0, 3).Select(i => Message($"a{i}", $"a-{i}") with { GroupKey = "a" }).ToList(), TestCancellationToken);

        var first = await receiver.ReceiveAsync(10, DeliveryTimeout, TestCancellationToken);
        Assert.Equal("a-0", first[0].MessageId);
        foreach (var message in first.Reverse())
            await receiver.AbandonAsync(message, TimeSpan.Zero, TestCancellationToken);

        var order = new List<string>();
        var deadline = DateTime.UtcNow + DeliveryTimeout;
        while (order.Count < 3 && DateTime.UtcNow < deadline)
        {
            foreach (var message in await receiver.ReceiveAsync(10, TimeSpan.FromSeconds(1), TestCancellationToken))
            {
                order.Add(message.MessageId);
                await receiver.CompleteAsync(message, TestCancellationToken);
            }
        }

        Assert.Equal(["a-0", "a-1", "a-2"], order);
    }

    /// <summary>A delayed message holds back the group's later messages until it is delivered.</summary>
    [Fact]
    public async Task OrderedGroups_ADelayedMessageHoldsBackItsGroup()
    {
        Assert.SkipUnless(Transport.Capabilities.OrderedGroups, "The transport does not order groups.");
        Assert.SkipWhen(Transport.Capabilities.MaxDelay == TimeSpan.Zero, "The transport does not support delays.");
        var source = MessageSource.Queue(NewName("group-delay"));
        await using var receiver = await CreateReceiverAsync(source);
        var stopwatch = Stopwatch.StartNew();
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [
            Message("first", "a-0") with { GroupKey = "a", DeliverAt = DateTimeOffset.UtcNow.Add(ShortDelay) },
            Message("second", "a-1") with { GroupKey = "a" }
        ], TestCancellationToken);

        var order = new List<string>();
        var deadline = DateTime.UtcNow + ShortDelay + DeliveryTimeout;
        while (order.Count < 2 && DateTime.UtcNow < deadline)
        {
            foreach (var message in await receiver.ReceiveAsync(10, TimeSpan.FromSeconds(1), TestCancellationToken))
            {
                order.Add(message.MessageId);
                await receiver.CompleteAsync(message, TestCancellationToken);
            }
        }

        Assert.Equal(["a-0", "a-1"], order);
        Assert.True(stopwatch.Elapsed >= ShortDelay * 0.75, $"The group was released after {stopwatch.Elapsed}, before its delayed first message was due.");
    }

    /// <summary>A dead-letter queue does not hold groups back, so all dead letters of one group are received together.</summary>
    [Fact]
    public async Task DeadLetters_OfOneGroup_CanAllBeReceivedAtOnce()
    {
        var source = MessageSource.Queue(NewName("group-dlq"));
        await using var receiver = await CreateReceiverAsync(source);
        await Transport.SendAsync(MessageDestination.Queue(source.Name),
            Enumerable.Range(0, 3).Select(i => Message($"a{i}", $"a-{i}") with { GroupKey = "a" }).ToList(), TestCancellationToken);
        for (int i = 0; i < 3; i++)
            await receiver.DeadLetterAsync(await ReceiveOneAsync(receiver), "conformance", TestCancellationToken);

        await using var deadLetters = await CreateReceiverAsync(MessageSource.DeadLetter(source));
        var messages = await ReceiveAsync(deadLetters, 3);

        Assert.Equal(["a-0", "a-1", "a-2"], messages.Select(m => m.MessageId).Order());
        Assert.All(messages, m => Assert.Equal("a", m.GroupKey));
    }

    /// <summary>A message whose deduplication id was recently sent to the same destination is dropped.</summary>
    [Fact]
    public async Task Deduplication_DropsMessagesWithASeenId()
    {
        Assert.SkipUnless(Transport.Capabilities.Deduplication, "The transport does not deduplicate.");
        var source = MessageSource.Queue(NewName("dedupe"));
        await using var receiver = await CreateReceiverAsync(source);

        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("one", "first") with { DeduplicationId = "same" }], TestCancellationToken);
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("two", "second") with { DeduplicationId = "same" }], TestCancellationToken);
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("canary", "canary") with { DeduplicationId = "other" }], TestCancellationToken);

        var messages = await ReceiveUntilAsync(receiver, "canary");
        Assert.Equal(["canary", "first"], messages.Select(m => m.MessageId).Order());
        foreach (var message in messages)
            await receiver.CompleteAsync(message, TestCancellationToken);
    }

    /// <summary>A repeated deduplication id is dropped within one batch, and when another process sends it.</summary>
    [Fact]
    public async Task Deduplication_InTheSameBatchOrFromAnotherProcess_DropsTheRepeat()
    {
        Assert.SkipUnless(Transport.Capabilities.Deduplication, "The transport does not deduplicate.");
        var source = MessageSource.Queue(NewName("dedupe-scope"));
        await using var receiver = await CreateReceiverAsync(source);
        var destination = MessageDestination.Queue(source.Name);

        await Transport.SendAsync(destination, [Message("one", "first") with { DeduplicationId = "same" }, Message("two", "second") with { DeduplicationId = "same" }], TestCancellationToken);
        await PeerTransport.SendAsync(destination, [Message("three", "third") with { DeduplicationId = "same" }], TestCancellationToken);
        await Transport.SendAsync(destination, [Message("canary", "canary")], TestCancellationToken);

        var messages = await ReceiveUntilAsync(receiver, "canary");
        Assert.Equal(["canary", "first"], messages.Select(m => m.MessageId).Order());
    }

    /// <summary>A deduplication id seen on one destination does not drop a message sent to another.</summary>
    [Fact]
    public async Task Deduplication_OnAnotherDestination_DeliversTheMessage()
    {
        Assert.SkipUnless(Transport.Capabilities.Deduplication, "The transport does not deduplicate.");
        var source = MessageSource.Queue(NewName("dedupe-a"));
        var other = MessageSource.Queue(NewName("dedupe-b"));
        await using var receiver = await CreateReceiverAsync(source);
        await using var otherReceiver = await CreateReceiverAsync(other);

        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("here", "here") with { DeduplicationId = "same" }], TestCancellationToken);
        await Transport.SendAsync(MessageDestination.Queue(other.Name), [Message("there", "there") with { DeduplicationId = "same" }], TestCancellationToken);

        Assert.Equal("here", (await ReceiveOneAsync(receiver)).MessageId);
        Assert.Equal("there", (await ReceiveOneAsync(otherReceiver)).MessageId);
    }

    /// <summary>A send that failed does not remember its deduplication ids, so the caller's retry is delivered.</summary>
    [Fact]
    public async Task Deduplication_AfterAFailedSend_DeliversTheRetry()
    {
        Assert.SkipUnless(Transport.Capabilities.Deduplication, "The transport does not deduplicate.");
        var source = MessageSource.Queue(NewName("dedupe-retry"));
        await using var receiver = await CreateReceiverAsync(source);
        var destination = MessageDestination.Queue(source.Name);

        using (var cancelled = new CancellationTokenSource())
        {
            await cancelled.CancelAsync();
            await SendIgnoringFailureAsync<OperationCanceledException>(destination, [Message("cancelled", "cancelled") with { DeduplicationId = "cancelled" }], cancelled.Token);
        }

        await Transport.SendAsync(destination, [Message("cancelled retry", "cancelled-retry") with { DeduplicationId = "cancelled" }], TestCancellationToken);

        if (Transport.Capabilities.MaxMessageSize is { } limit)
        {
            var tooLarge = new OutgoingMessage { MessageId = "too-large", Body = new byte[(int)limit + 1] };
            await SendIgnoringFailureAsync<MessageTooLargeException>(destination, [Message("rejected", "rejected") with { DeduplicationId = "rejected" }, tooLarge], TestCancellationToken);
            await Transport.SendAsync(destination, [Message("rejected retry", "rejected-retry") with { DeduplicationId = "rejected" }], TestCancellationToken);
        }

        await Transport.SendAsync(destination, [Message("canary", "canary")], TestCancellationToken);

        // Whether or not the failed send got its message through, exactly one message per deduplication id arrives.
        var ids = (await ReceiveUntilAsync(receiver, "canary")).Select(m => m.MessageId).ToList();
        Assert.Single(ids, id => id.StartsWith("cancelled", StringComparison.Ordinal));
        if (Transport.Capabilities.MaxMessageSize is not null)
            Assert.Single(ids, id => id.StartsWith("rejected", StringComparison.Ordinal));
    }

    /// <summary>A queue or subscription that does not exist yet counts as empty instead of throwing.</summary>
    [Fact]
    public async Task GetStats_OfASourceThatDoesNotExist_CountsAsEmpty()
    {
        var queue = MessageSource.Queue(NewName("stats-none"));
        var subscription = MessageSource.Subscription(NewName("stats-none-topic"), "none");
        DeleteAfterTest(queue);
        DeleteAfterTest(subscription);

        foreach (var source in new[] { queue, subscription })
        {
            var stats = await Transport.GetStatsAsync(source, TestCancellationToken);
            if (stats is null)
                continue;

            Assert.Equal(source, stats.Source);
            AssertCount(0, stats.Queued, source, "queued");
            AssertCount(0, stats.Delayed, source, "delayed");
            AssertCount(0, stats.InFlight, source, "in flight");
            AssertCount(0, stats.DeadLettered, source, "dead-lettered");
        }
    }

    /// <summary>Messages sent to a queue count as queued until they are received.</summary>
    [Fact]
    public async Task GetStats_AfterSendsToAQueue_CountsThemAsQueued()
    {
        var source = MessageSource.Queue(NewName("stats-queued"));
        DeleteAfterTest(source);
        Assert.SkipWhen((await GetStatsOrSkipAsync(source)).Queued is null, "The transport does not count queued messages.");

        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("a"), Message("b"), Message("c")], TestCancellationToken);

        await WaitForStatsAsync(source, "3 queued and none in flight", s => s.Queued == 3 && Is(s.InFlight, 0) && Is(s.Delayed, 0));
    }

    /// <summary>
    /// A received message that is not settled yet counts as in flight instead of queued, and as neither once it is
    /// completed. A transport that cannot count in-flight messages may keep counting it as queued until it is settled.
    /// </summary>
    [Fact]
    public async Task GetStats_WhileAMessageIsReceivedAndNotSettled_CountsItInFlightInsteadOfQueued()
    {
        var source = MessageSource.Queue(NewName("stats-inflight"));
        DeleteAfterTest(source);
        var initial = await GetStatsOrSkipAsync(source);
        Assert.SkipWhen(initial.InFlight is null && initial.Queued is null, "The transport counts neither queued nor in-flight messages.");
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("leased", "id-leased")], TestCancellationToken);
        await WaitForStatsAsync(source, "1 queued", s => Is(s.Queued, 1) && Is(s.InFlight, 0));

        await using var receiver = await CreateReceiverAsync(source);
        var message = await ReceiveOneAsync(receiver);

        await WaitForStatsAsync(source, "1 in flight and none queued", s => s.InFlight is null || s.InFlight == 1 && Is(s.Queued, 0));
        await receiver.CompleteAsync(message, TestCancellationToken);
        await WaitForStatsAsync(source, "none in flight or queued after the complete", s => Is(s.InFlight, 0) && Is(s.Queued, 0));
    }

    /// <summary>A message sent with a future delivery time counts as delayed, not queued.</summary>
    [Fact]
    public async Task GetStats_AfterADelayedSend_CountsItAsDelayed()
    {
        Assert.SkipWhen(Transport.Capabilities.MaxDelay == TimeSpan.Zero, "The transport does not support delays.");
        var source = MessageSource.Queue(NewName("stats-delayed"));
        DeleteAfterTest(source);
        Assert.SkipWhen((await GetStatsOrSkipAsync(source)).Delayed is null, "The transport does not count delayed messages.");
        var delay = Transport.Capabilities.MaxDelay is { } maxDelay && maxDelay < TimeSpan.FromMinutes(5) ? maxDelay : TimeSpan.FromMinutes(5);

        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("later") with { DeliverAt = DateTimeOffset.UtcNow.Add(delay) }], TestCancellationToken);

        await WaitForStatsAsync(source, "1 delayed and none queued", s => s.Delayed == 1 && Is(s.Queued, 0) && Is(s.InFlight, 0));
    }

    /// <summary>A dead-lettered message counts as dead-lettered, and no longer as queued or in flight.</summary>
    [Fact]
    public async Task GetStats_AfterADeadLetter_CountsItAsDeadLettered()
    {
        var source = MessageSource.Queue(NewName("stats-dlq"));
        await using var receiver = await CreateReceiverAsync(source);
        Assert.SkipWhen((await GetStatsOrSkipAsync(source)).DeadLettered is null, "The transport does not count dead letters.");
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("poison", "id-poison")], TestCancellationToken);

        await receiver.DeadLetterAsync(await ReceiveOneAsync(receiver), "conformance", TestCancellationToken);

        await WaitForStatsAsync(source, "1 dead-lettered and none queued or in flight", s => s.DeadLettered == 1 && Is(s.Queued, 0) && Is(s.InFlight, 0));
    }

    /// <summary>Each durable subscription counts the copies published to it, plus messages sent to it alone.</summary>
    [Fact]
    public async Task GetStats_OfEachSubscription_CountsItsOwnPublishedCopies()
    {
        string topic = NewName("stats-topic");
        var first = MessageSource.Subscription(topic, "first");
        var second = MessageSource.Subscription(topic, "second");
        await (await CreateReceiverAsync(first)).DisposeAsync();
        await (await CreateReceiverAsync(second)).DisposeAsync();
        Assert.SkipWhen((await GetStatsOrSkipAsync(first)).Queued is null, "The transport does not count queued messages.");

        await Transport.SendAsync(MessageDestination.Topic(topic), [Message("one"), Message("two")], TestCancellationToken);
        await Transport.SendAsync(MessageDestination.Subscription(topic, "first"), [Message("only first")], TestCancellationToken);

        await WaitForStatsAsync(first, "3 queued", s => s.Queued == 3);
        await WaitForStatsAsync(second, "2 queued", s => s.Queued == 2);
    }

    /// <summary>A dead-letter source has no counts of its own: its count is the DeadLettered of the source it belongs to.</summary>
    [Fact]
    public async Task GetStats_OfADeadLetterSource_ThrowsArgumentException()
    {
        var queue = MessageSource.Queue(NewName("stats-dlq-source"));
        DeleteAfterTest(queue);

        await Assert.ThrowsAsync<ArgumentException>(() => Transport.GetStatsAsync(MessageSource.DeadLetter(queue), TestCancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => Transport.GetStatsAsync(MessageSource.DeadLetter(MessageSource.Subscription(NewName("stats-dlq-topic"), "s")), TestCancellationToken));
    }

    /// <summary>An instance subscription may be uncountable: the call returns null, or counts that are never negative, without throwing.</summary>
    [Fact]
    public async Task GetStats_OfAnInstanceSubscription_ReturnsNullOrCounts()
    {
        var source = MessageSource.InstanceSubscription(NewName("stats-instances"), "conformance");
        await using var receiver = await CreateReceiverAsync(source);

        var stats = await Transport.GetStatsAsync(source, TestCancellationToken);

        if (stats is null)
            return;

        Assert.Equal(source, stats.Source);
        Assert.All(new[] { stats.Queued, stats.Delayed, stats.InFlight, stats.DeadLettered }, count => Assert.True(count is null or >= 0, $"A count was {count}."));
    }

    /// <summary>End to end: a sent message is handled once across competing hosts on this transport.</summary>
    [Fact]
    public async Task Messaging_SentMessageIsHandledOnceByCompetingServices()
    {
        string name = NewName("cmd");
        DeleteAfterTest(MessageSource.Queue(name));
        var handled = new ConcurrentBag<int>();
        Action<FoundatioMessagingBuilder> configure = m => m.MapMessage<ConformanceMessage>(name).AddConsumer<ConformanceMessage>((message, _, _) =>
        {
            handled.Add(message.Value);
            return Task.CompletedTask;
        });

        await using var first = await MessagingTestHost.StartAsync(Transport, Log, "worker", configure);
        await using var second = await MessagingTestHost.StartAsync(PeerTransport, Log, "worker", configure);
        for (int i = 0; i < 5; i++)
            await first.Client.SendAsync(new ConformanceMessage(i), cancellationToken: TestCancellationToken);

        await WaitForAsync(() => handled.Count >= 5);
        await second.Client.SendAsync(new ConformanceMessage(99), cancellationToken: TestCancellationToken);
        await WaitForAsync(() => handled.Contains(99));

        Assert.Equal([0, 1, 2, 3, 4, 99], handled.Order());
    }

    /// <summary>End to end: a published event reaches every service's subscriber once, and every instance subscriber.</summary>
    [Fact]
    public async Task Messaging_PublishedEventReachesEveryServiceAndEveryInstance()
    {
        string name = NewName("evt");
        DeleteAfterTest(MessageSource.Subscription(name, "billing"));
        DeleteAfterTest(MessageSource.Subscription(name, "shipping"));
        var received = new ConcurrentBag<string>();
        Action<FoundatioMessagingBuilder> Configure(string label) => m => m
            .MapMessage<ConformanceMessage>(name)
            .AddSubscriber<ConformanceMessage>((message, _, _) => { received.Add($"{label}:service:{message.Value}"); return Task.CompletedTask; })
            .AddSubscriber<ConformanceMessage>((message, _, _) => { received.Add($"{label}:instance:{message.Value}"); return Task.CompletedTask; }, new SubscriptionOptions { Scope = SubscriptionScope.Instance });

        await using var billing1 = await MessagingTestHost.StartAsync(Transport, Log, "billing", Configure("billing1"));
        await using var billing2 = await MessagingTestHost.StartAsync(PeerTransport, Log, "billing", Configure("billing2"));
        await using var shipping = await MessagingTestHost.StartAsync(Transport, Log, "shipping", Configure("shipping"));

        await billing1.Client.PublishAsync(new ConformanceMessage(1), cancellationToken: TestCancellationToken);
        await WaitForAsync(() => received.Count(r => r.EndsWith(":1")) >= 5);
        await billing1.Client.PublishAsync(new ConformanceMessage(2), cancellationToken: TestCancellationToken);
        await WaitForAsync(() => received.Count(r => r.EndsWith(":2")) >= 5);

        foreach (int value in new[] { 1, 2 })
        {
            var copies = received.Where(r => r.EndsWith($":{value}")).ToList();
            Assert.Equal(1, copies.Count(r => r.StartsWith("billing") && r.Contains(":service:")));
            Assert.Equal(1, copies.Count(r => r.StartsWith("shipping:service:")));
            Assert.Equal([$"billing1:instance:{value}", $"billing2:instance:{value}", $"shipping:instance:{value}"], copies.Where(r => r.Contains(":instance:")).Order());
        }
    }

    /// <summary>End to end: a handler that keeps throwing is retried, then its message is dead-lettered.</summary>
    [Fact]
    public async Task Messaging_FailingMessageIsRetriedThenDeadLettered()
    {
        string name = NewName("poison");
        DeleteAfterTest(MessageSource.Queue(name));
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

    /// <summary>End to end: a delayed send is handled no earlier than its delay.</summary>
    [Fact]
    public async Task Messaging_DelayedSendIsHandledAfterTheDelay()
    {
        Assert.SkipWhen(Transport.Capabilities.MaxDelay == TimeSpan.Zero, "The transport does not support delays.");
        string name = NewName("later");
        DeleteAfterTest(MessageSource.Queue(name));
        var handledAt = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopwatch = Stopwatch.StartNew();
        await using var host = await MessagingTestHost.StartAsync(Transport, Log, "worker", m => m
            .MapMessage<ConformanceMessage>(name)
            .AddConsumer<ConformanceMessage>((_, _, _) => { handledAt.TrySetResult(stopwatch.Elapsed); return Task.CompletedTask; }));

        await host.Client.SendAsync(new ConformanceMessage(1), new SendOptions { Delay = ShortDelay }, TestCancellationToken);

        var elapsed = await handledAt.Task.WaitAsync(ShortDelay + DeliveryTimeout, TestCancellationToken);
        Assert.True(elapsed >= ShortDelay * 0.75, $"Handled after {elapsed}, expected at least {ShortDelay}.");
    }

    /// <summary>End to end: dead letters replayed through <see cref="IMessageAdmin"/> are handled again.</summary>
    [Fact]
    public async Task Messaging_ReplayedDeadLettersAreHandledAgain()
    {
        string name = NewName("replay");
        int attempts = 0;
        var handled = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await MessagingTestHost.StartAsync(Transport, Log, "worker", m => m
            .MapMessage<ConformanceMessage>(name)
            .AddSubscriber<ConformanceMessage>((message, _, _) =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                    throw new InvalidOperationException("fails the first time");

                handled.TrySetResult(message.Value);
                return Task.CompletedTask;
            }, new SubscriptionOptions { RetryPolicy = RetryPolicy.None }));

        var admin = host.Services.GetRequiredService<IMessageAdmin>();
        var source = MessageSource.Subscription(name, "worker");
        DeleteAfterTest(source);
        await host.Client.PublishAsync(new ConformanceMessage(7), cancellationToken: TestCancellationToken);

        IReadOnlyList<DeadLetterMessage> deadLetters;
        var deadline = DateTime.UtcNow + DeliveryTimeout * 2;
        while ((deadLetters = await admin.PeekDeadLettersAsync(source, cancellationToken: TestCancellationToken)).Count == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "The message was never dead-lettered.");
            await Task.Delay(100, TestCancellationToken);
        }

        Assert.Single(deadLetters);
        Assert.Equal(new ConformanceMessage(7), deadLetters[0].Message);
        Assert.Contains("fails the first time", deadLetters[0].Reason);

        Assert.Equal(1, await admin.ReplayDeadLettersAsync(source, cancellationToken: TestCancellationToken));
        Assert.Equal(7, await handled.Task.WaitAsync(DeliveryTimeout, TestCancellationToken));
        Assert.Empty(await admin.PeekDeadLettersAsync(source, cancellationToken: TestCancellationToken));
    }

    /// <summary>Deletes the sources the test created, then disposes every transport it tracked.</summary>
    public override async ValueTask DisposeAsync()
    {
        if (_transport is not null && !_createdSources.IsEmpty)
            await DeleteCreatedSourcesAsync(_transport);

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

    /// <summary>Disposes <paramref name="transport"/> when the test ends, and returns it.</summary>
    protected T Track<T>(T transport) where T : IMessageTransport
    {
        _transports.Add(transport);
        return transport;
    }

    /// <summary>
    /// Deletes the queue or subscription, with its dead letters, once the test ends. <see cref="CreateReceiverAsync"/>
    /// and <see cref="CreatePeerReceiverAsync"/> already do this for the sources they open.
    /// </summary>
    protected void DeleteAfterTest(MessageSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Kind == MessageSourceKind.InstanceSubscription)
            return;

        _createdSources.TryAdd(source.ToString(), source);
    }

    /// <summary>A unique, valid name so concurrent tests never share queues or topics.</summary>
    protected static string NewName(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..(prefix.Length + 13)];

    /// <summary>Opens a receiver through <see cref="Transport"/> with <paramref name="lease"/> (30 seconds by default), and deletes the source after the test.</summary>
    protected Task<IMessageReceiver> CreateReceiverAsync(MessageSource source, TimeSpan? lease = null)
    {
        DeleteAfterTest(source);
        return Transport.CreateReceiverAsync(source, new ReceiverOptions { LeaseDuration = lease ?? TimeSpan.FromSeconds(30) }, TestCancellationToken);
    }

    /// <summary>Opens a receiver through <see cref="PeerTransport"/>, as another process would.</summary>
    protected Task<IMessageReceiver> CreatePeerReceiverAsync(MessageSource source, TimeSpan? lease = null)
    {
        DeleteAfterTest(source);
        return PeerTransport.CreateReceiverAsync(source, new ReceiverOptions { LeaseDuration = lease ?? TimeSpan.FromSeconds(30) }, TestCancellationToken);
    }

    /// <summary>A message with a UTF-8 <paramref name="body"/>, and <paramref name="id"/> or a new one.</summary>
    protected static OutgoingMessage Message(string body, string? id = null, IReadOnlyDictionary<string, string>? headers = null)
    {
        return new OutgoingMessage
        {
            MessageId = id ?? Guid.NewGuid().ToString("N"),
            Body = Encoding.UTF8.GetBytes(body),
            Headers = headers ?? new Dictionary<string, string>()
        };
    }

    /// <summary>Receives one message, failing after <paramref name="timeout"/> (<see cref="DeliveryTimeout"/> by default).</summary>
    protected async Task<IncomingMessage> ReceiveOneAsync(IMessageReceiver receiver, TimeSpan? timeout = null)
    {
        return (await ReceiveAsync(receiver, 1, timeout)).Single();
    }

    /// <summary>Receives <paramref name="count"/> messages without settling them, failing after <paramref name="timeout"/> (<see cref="DeliveryTimeout"/> by default).</summary>
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

    /// <summary>
    /// Receives until the message with <paramref name="messageId"/> arrives and returns everything received up to and
    /// including its batch, without settling anything. Send that message after the ones a test expects not to see, so
    /// the receiver has demonstrably been delivering while they could have arrived.
    /// </summary>
    protected async Task<IReadOnlyList<IncomingMessage>> ReceiveUntilAsync(IMessageReceiver receiver, string messageId, TimeSpan? timeout = null)
    {
        var messages = new List<IncomingMessage>();
        var deadline = DateTime.UtcNow + (timeout ?? DeliveryTimeout);
        while (!messages.Any(m => m.MessageId == messageId))
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException($"Message {messageId} did not arrive from {receiver.Source} within {timeout ?? DeliveryTimeout}; received {String.Join(", ", messages.Select(m => m.MessageId))}.");

            messages.AddRange(await receiver.ReceiveAsync(10, remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1), TestCancellationToken));
        }

        return messages;
    }

    /// <summary>Asserts that the next message from <paramref name="receiver"/> is <paramref name="messageId"/>, with nothing before it.</summary>
    protected async Task<IReadOnlyList<IncomingMessage>> AssertOnlyReceivedAsync(IMessageReceiver receiver, string messageId, bool complete = true)
    {
        var messages = await ReceiveUntilAsync(receiver, messageId);
        Assert.Equal([messageId], messages.Select(m => m.MessageId));
        if (complete)
        {
            foreach (var message in messages)
                await receiver.CompleteAsync(message, TestCancellationToken);
        }

        return messages;
    }

    /// <summary>Fails if <paramref name="receiver"/> receives anything within <paramref name="duration"/>.</summary>
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

    /// <summary>Polls <paramref name="condition"/> until it holds, failing after <paramref name="timeout"/> (<see cref="DeliveryTimeout"/> by default).</summary>
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

    private async Task AssertStaleSettlementLeavesTheNewDeliveryIntactAsync(MessageSource source, IMessageReceiver staleReceiver, IncomingMessage stale,
        IMessageReceiver currentReceiver, IncomingMessage current, bool receiverDisposed)
    {
        await AssertLeaseLostAsync(() => staleReceiver.RenewAsync(stale, TestCancellationToken), receiverDisposed);
        await AssertLeaseLostAsync(() => staleReceiver.AbandonAsync(stale, TimeSpan.Zero, TestCancellationToken), receiverDisposed);
        await AssertLeaseLostAsync(() => staleReceiver.DeadLetterAsync(stale, "stale", TestCancellationToken), receiverDisposed);
        bool staleCompleteFailed = await TryAssertLeaseLostAsync(() => staleReceiver.CompleteAsync(stale, TestCancellationToken), receiverDisposed);

        // The stale abandon must not have released the newer delivery: a message sent afterwards arrives alone.
        await Transport.SendAsync(MessageDestination.Queue(source.Name), [Message("canary", "canary")], TestCancellationToken);
        var canary = Assert.Single(await AssertOnlyReceivedAsync(currentReceiver, "canary", complete: false));

        if (staleCompleteFailed)
        {
            await currentReceiver.RenewAsync(current, TestCancellationToken);
            await currentReceiver.AbandonAsync(current, TimeSpan.Zero, TestCancellationToken);
            var again = await ReceiveOneAsync(currentReceiver);
            Assert.Equal(current.MessageId, again.MessageId);
            Assert.True(again.DeliveryCount > current.DeliveryCount, $"DeliveryCount stayed at {again.DeliveryCount} after the abandon.");
            await currentReceiver.CompleteAsync(again, TestCancellationToken);
        }
        else
        {
            // The broker could not tell the stale receipt from the current one, so the message may be gone.
            _logger.LogInformation("The stale complete of {MessageId} was accepted", stale.MessageId);
            await TryAssertLeaseLostAsync(() => currentReceiver.CompleteAsync(current, TestCancellationToken), receiverDisposed: false);
        }

        // Nor may the stale dead-letter have moved it.
        await currentReceiver.DeadLetterAsync(canary, "canary", TestCancellationToken);
        await using var deadLetters = await CreateReceiverAsync(MessageSource.DeadLetter(source));
        await AssertOnlyReceivedAsync(deadLetters, "canary");
    }

    /// <summary>Asserts that a settlement of a delivery whose lease was lost throws, as the contract requires.</summary>
    private static async Task AssertLeaseLostAsync(Func<Task> settle, bool receiverDisposed)
    {
        Assert.True(await TryAssertLeaseLostAsync(settle, receiverDisposed), "Settling or renewing a delivery whose lease was lost succeeded instead of throwing MessageLeaseLostException.");
    }

    /// <summary>Returns false when the settlement succeeded, and fails the test when it threw anything but lease lost.</summary>
    private static async Task<bool> TryAssertLeaseLostAsync(Func<Task> settle, bool receiverDisposed)
    {
        try
        {
            await settle();
            return false;
        }
        catch (MessageLeaseLostException)
        {
            return true;
        }
        catch (ObjectDisposedException) when (receiverDisposed)
        {
            return true;
        }
        catch (Exception ex)
        {
            Assert.Fail($"Settling a delivery whose lease was lost threw {ex.GetType().Name} instead of MessageLeaseLostException: {ex.Message}");
            return false;
        }
    }

    /// <summary>Receives from every receiver at once, as competing consumers do, until one of them gets messages.</summary>
    private async Task<List<(IMessageReceiver Receiver, IncomingMessage Message)>> ReceiveFromAnyAsync(IReadOnlyList<IMessageReceiver> receivers, int maxMessages, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var batches = await Task.WhenAll(receivers.Select(r => r.ReceiveAsync(maxMessages, TimeSpan.FromSeconds(1), TestCancellationToken)));
            var deliveries = receivers.Zip(batches).SelectMany(p => p.Second.Select(m => (p.First, m))).ToList();
            if (deliveries.Count > 0)
                return deliveries;

            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"No receiver got a message within {timeout}.");
        }
    }

    /// <summary>Like <see cref="ReceiveUntilAsync"/>, across competing receivers, in the order they were received.</summary>
    private async Task<List<(IMessageReceiver Receiver, IncomingMessage Message)>> ReceiveFromAnyUntilAsync(IReadOnlyList<IMessageReceiver> receivers, string messageId, TimeSpan timeout)
    {
        var deliveries = new List<(IMessageReceiver Receiver, IncomingMessage Message)>();
        var deadline = DateTime.UtcNow + timeout;
        while (!deliveries.Any(d => d.Message.MessageId == messageId))
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException($"Message {messageId} did not arrive within {timeout}; received {String.Join(", ", deliveries.Select(d => d.Message.MessageId))}.");

            deliveries.AddRange(await ReceiveFromAnyAsync(receivers, 10, remaining));
        }

        return deliveries;
    }

    /// <summary>Receives a group's remaining messages from competing receivers, settling each, and returns their order.</summary>
    private async Task<List<string>> DrainGroupInOrderAsync(IReadOnlyList<IMessageReceiver> receivers, string groupKey, int count)
    {
        var order = new List<string>();
        var deadline = DateTime.UtcNow + DeliveryTimeout;
        while (order.Count < count)
        {
            var deliveries = await ReceiveFromAnyAsync(receivers, 10, deadline - DateTime.UtcNow);
            var grouped = deliveries.Where(d => d.Message.GroupKey == groupKey).ToList();
            Assert.True(grouped.Select(d => d.Receiver).Distinct().Count() <= 1, $"Messages of group {groupKey} were delivered to two receivers at once.");
            foreach (var (receiver, message) in deliveries)
            {
                if (message.GroupKey == groupKey)
                    order.Add(message.MessageId);

                await receiver.CompleteAsync(message, TestCancellationToken);
            }
        }

        return order;
    }

    /// <summary>Reads the counts of <paramref name="source"/>, skipping the test when the transport cannot count it.</summary>
    private async Task<MessageSourceStats> GetStatsOrSkipAsync(MessageSource source)
    {
        var stats = await Transport.GetStatsAsync(source, TestCancellationToken);
        Assert.SkipWhen(stats is null, $"The transport does not count {source.Kind} sources.");
        Assert.Equal(source, stats!.Source);
        return stats;
    }

    /// <summary>Polls the counts of <paramref name="source"/> until <paramref name="condition"/> holds, failing after <see cref="StatsTimeout"/>.</summary>
    private async Task<MessageSourceStats> WaitForStatsAsync(MessageSource source, string expected, Func<MessageSourceStats, bool> condition)
    {
        var deadline = DateTime.UtcNow + StatsTimeout;
        while (true)
        {
            var stats = await GetStatsOrSkipAsync(source);
            if (condition(stats))
                return stats;

            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail($"{source} did not count {expected} within {StatsTimeout}; it counted {Describe(stats.Queued)} queued, {Describe(stats.Delayed)} delayed, " +
                    $"{Describe(stats.InFlight)} in flight and {Describe(stats.DeadLettered)} dead-lettered.");
            }

            await Task.Delay(100, TestCancellationToken);
        }

        static string Describe(long? count) => count?.ToString() ?? "(not counted)";
    }

    /// <summary>True when the transport does not report <paramref name="count"/>, or reports <paramref name="expected"/>.</summary>
    private static bool Is(long? count, long expected) => count is null || count == expected;

    private static void AssertCount(long expected, long? count, MessageSource source, string what)
    {
        if (count is not null)
            Assert.True(count == expected, $"{source} counted {count} {what}; expected {expected}.");
    }

    private async Task SendIgnoringFailureAsync<TException>(MessageDestination destination, IReadOnlyList<OutgoingMessage> messages, CancellationToken cancellationToken) where TException : Exception
    {
        try
        {
            await Transport.SendAsync(destination, messages, cancellationToken);
        }
        catch (TException ex)
        {
            _logger.LogInformation("The send failed as intended: {Message}", ex.Message);
        }
    }

    private async Task DeleteCreatedSourcesAsync(IMessageTransport transport)
    {
        using var timeout = new CancellationTokenSource(CleanupTimeout);
        var sources = _createdSources.Values
            .Select(s => s.Kind == MessageSourceKind.DeadLetter && _createdSources.ContainsKey(s.DeadLetterOf!.ToString()) ? null : s)
            .OfType<MessageSource>();

        await Task.WhenAll(sources.Select(async source =>
        {
            try
            {
                await transport.DeleteAsync(source, timeout.Token);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not delete {Source} after the test", source);
            }
        }));
    }

    private static Dictionary<string, string> ManyHeaders()
    {
        var headers = new Dictionary<string, string> { [KnownHeaders.MessageType] = "conformance.size", [KnownHeaders.CorrelationId] = "size-test" };
        for (int i = 0; i < 100; i++)
            headers[$"h{i}"] = "v";

        return headers;
    }

    private static string PadToMaxIdLength(string prefix, char fill) => prefix + new string(fill, OutgoingMessage.MaxIdLength - prefix.Length);

    /// <summary>The message the end-to-end tests send.</summary>
    /// <param name="Value">Tells the messages of a test apart.</param>
    public sealed record ConformanceMessage(int Value);
}
