using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;
using Xunit;

namespace Foundatio.Tests.Messaging;

/// <summary>Runs the original message bus suite against the legacy adapter over the new in-memory transport.</summary>
public class LegacyMessageBusAdapterTests : MessageBusTestBase, IAsyncDisposable
{
    private readonly InMemoryMessageTransport _transport = new();
    private IMessageBus? _messageBus;

    public LegacyMessageBusAdapterTests(ITestOutputHelper output) : base(output) { }

    protected override IMessageBus GetMessageBus(Func<SharedMessageBusOptions, SharedMessageBusOptions>? config = null)
    {
        if (_messageBus is not null)
            return _messageBus;

        var options = new SharedMessageBusOptions { LoggerFactory = Log };
        config?.Invoke(options);
        _messageBus = new LegacyMessageBusAdapter(_transport, options);
        return _messageBus;
    }

    [Fact]
    public override Task CanUseMessageOptionsAsync()
    {
        return base.CanUseMessageOptionsAsync();
    }

    [Fact]
    public override Task CanSendMessageAsync()
    {
        return base.CanSendMessageAsync();
    }

    [Fact]
    public override Task CanHandleNullMessageAsync()
    {
        return base.CanHandleNullMessageAsync();
    }

    [Fact]
    public override Task CanSendDerivedMessageAsync()
    {
        return base.CanSendDerivedMessageAsync();
    }

    [Fact]
    public override Task CanSendMappedMessageAsync()
    {
        return base.CanSendMappedMessageAsync();
    }

    [Fact]
    public override Task CanSendDelayedMessageAsync()
    {
        return base.CanSendDelayedMessageAsync();
    }

    [Fact]
    public override Task CanSubscribeConcurrentlyAsync()
    {
        return base.CanSubscribeConcurrentlyAsync();
    }

    [Fact]
    public override Task CanReceiveMessagesConcurrentlyAsync()
    {
        return base.CanReceiveMessagesConcurrentlyAsync();
    }

    [Fact]
    public override Task CanSendMessageToMultipleSubscribersAsync()
    {
        return base.CanSendMessageToMultipleSubscribersAsync();
    }

    [Fact]
    public override Task CanTolerateSubscriberFailureAsync()
    {
        return base.CanTolerateSubscriberFailureAsync();
    }

    [Fact]
    public override Task WillOnlyReceiveSubscribedMessageTypeAsync()
    {
        return base.WillOnlyReceiveSubscribedMessageTypeAsync();
    }

    [Fact]
    public override Task WillReceiveDerivedMessageTypesAsync()
    {
        return base.WillReceiveDerivedMessageTypesAsync();
    }

    [Fact]
    public override Task CanSubscribeToAllMessageTypesAsync()
    {
        return base.CanSubscribeToAllMessageTypesAsync();
    }

    [Fact]
    public override Task CanSubscribeToRawMessagesAsync()
    {
        return base.CanSubscribeToRawMessagesAsync();
    }

    [Fact]
    public override Task CanCancelSubscriptionAsync()
    {
        return base.CanCancelSubscriptionAsync();
    }

    [Fact]
    public override Task WontKeepMessagesWithNoSubscribersAsync()
    {
        return base.WontKeepMessagesWithNoSubscribersAsync();
    }

    [Fact]
    public override Task CanReceiveFromMultipleSubscribersAsync()
    {
        return base.CanReceiveFromMultipleSubscribersAsync();
    }

    [Fact]
    public override Task CanDisposeWithNoSubscribersOrPublishersAsync()
    {
        return base.CanDisposeWithNoSubscribersOrPublishersAsync();
    }

    [Fact]
    public override Task CanHandlePoisonedMessageAsync()
    {
        return base.CanHandlePoisonedMessageAsync();
    }

    [Fact]
    public override Task DisposeAsync_CalledMultipleTimes_IsIdempotentAsync()
    {
        return base.DisposeAsync_CalledMultipleTimes_IsIdempotentAsync();
    }

    [Fact]
    public override Task DisposeAsync_WhilePublishing_CompletesWithoutDeadlockAsync()
    {
        return base.DisposeAsync_WhilePublishing_CompletesWithoutDeadlockAsync();
    }

    [Fact]
    public override Task DisposeAsync_WithNoSubscribersOrPublishers_CompletesWithoutExceptionAsync()
    {
        return base.DisposeAsync_WithNoSubscribersOrPublishers_CompletesWithoutExceptionAsync();
    }

    [Fact]
    public override Task PublishAsync_AfterDispose_ThrowsMessageBusExceptionAsync()
    {
        return base.PublishAsync_AfterDispose_ThrowsMessageBusExceptionAsync();
    }

    [Fact]
    public override Task PublishAsync_WithCancellation_ThrowsOperationCanceledExceptionAsync()
    {
        return base.PublishAsync_WithCancellation_ThrowsOperationCanceledExceptionAsync();
    }

    [Fact]
    public override Task PublishAsync_WithDelayedMessageAndDisposeBeforeDelivery_DiscardsMessageAsync()
    {
        return base.PublishAsync_WithDelayedMessageAndDisposeBeforeDelivery_DiscardsMessageAsync();
    }

    [Fact]
    public override Task PublishAsync_WithSerializationFailure_ThrowsSerializerExceptionAsync()
    {
        return base.PublishAsync_WithSerializationFailure_ThrowsSerializerExceptionAsync();
    }

    [Fact]
    public override Task SubscribeAsync_AfterDispose_ThrowsMessageBusExceptionAsync()
    {
        return base.SubscribeAsync_AfterDispose_ThrowsMessageBusExceptionAsync();
    }

    [Fact]
    public override Task SubscribeAsync_CancelledToken_DoesNotTearDownInfrastructureAsync()
    {
        return base.SubscribeAsync_CancelledToken_DoesNotTearDownInfrastructureAsync();
    }

    [Fact]
    public override Task SubscribeAsync_WithCancellation_ThrowsOperationCanceledExceptionAsync()
    {
        return base.SubscribeAsync_WithCancellation_ThrowsOperationCanceledExceptionAsync();
    }

    [Fact]
    public override Task SubscribeAsync_WithDeserializationFailure_SkipsMessageAsync()
    {
        return base.SubscribeAsync_WithDeserializationFailure_SkipsMessageAsync();
    }

    [Fact]
    public override Task SubscribeAsync_WithValidThenPoisonedMessage_DeliversOnlyValidMessageAsync()
    {
        return base.SubscribeAsync_WithValidThenPoisonedMessage_DeliversOnlyValidMessageAsync();
    }

    [Fact]
    public override Task PublishAsync_WithDeliveryDelayExtension_DelaysDeliveryAsync()
    {
        return base.PublishAsync_WithDeliveryDelayExtension_DelaysDeliveryAsync();
    }

    [Fact]
    public override Task PublishAsync_WithUniqueId_PropagatesUniqueIdToSubscriberAsync()
    {
        return base.PublishAsync_WithUniqueId_PropagatesUniqueIdToSubscriberAsync();
    }

    [Fact]
    public override Task SubscribeAsync_ToRawIMessage_CanAccessAllPropertiesAsync()
    {
        return base.SubscribeAsync_ToRawIMessage_CanAccessAllPropertiesAsync();
    }

    [Fact]
    public override Task SubscribeAsync_WithCancellationTokenHandler_ReceivesCancellationTokenAsync()
    {
        return base.SubscribeAsync_WithCancellationTokenHandler_ReceivesCancellationTokenAsync();
    }

    [Fact]
    public async Task SubscribeAsync_WhenTheReceiverFaults_ReplacesItAndKeepsReceiving()
    {
        var transport = new InterceptingMessageTransport(_transport);
        await using var bus = new LegacyMessageBusAdapter(transport, new SharedMessageBusOptions { LoggerFactory = Log, Topic = "legacy-faults" });
        var received = new ConcurrentQueue<int>();
        await bus.SubscribeAsync<LegacyEvent>(e => received.Enqueue(e.Id), TestCancellationToken);

        await bus.PublishAsync(new LegacyEvent(1), cancellationToken: TestCancellationToken);
        await WaitForAsync(() => received.Count == 1);
        transport.FaultReceivers();
        await WaitForAsync(() => transport.ReceiversCreated > 1 && transport.OpenReceivers == 1);
        await bus.PublishAsync(new LegacyEvent(2), cancellationToken: TestCancellationToken);

        await WaitForAsync(() => received.Contains(2));
    }

    [Fact]
    public async Task SubscribeAsync_WithASlowSubscriber_DeliversToOtherSubscribersAndLaterMessagesMeanwhile()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var bus = new LegacyMessageBusAdapter(_transport, new SharedMessageBusOptions { LoggerFactory = Log, Topic = "legacy-slow" });
        var fast = new ConcurrentQueue<int>();
        await bus.SubscribeAsync<LegacyEvent>(async (_, ct) => await release.Task.WaitAsync(ct), TestCancellationToken);
        await bus.SubscribeAsync<LegacyEvent>(e => fast.Enqueue(e.Id), TestCancellationToken);

        await bus.PublishAsync(new LegacyEvent(1), cancellationToken: TestCancellationToken);
        await bus.PublishAsync(new LegacyEvent(2), cancellationToken: TestCancellationToken);

        await WaitForAsync(() => fast.Count == 2);
        release.SetResult();
    }

    [Fact]
    public async Task SubscribeAsync_WithASubscriberSlowerThanTheLease_ReceivesEachMessageOnce()
    {
        var transport = new InterceptingMessageTransport(_transport, _transport.Capabilities with { MaxLeaseDuration = TimeSpan.FromSeconds(1) });
        await using var bus = new LegacyMessageBusAdapter(transport, new SharedMessageBusOptions { LoggerFactory = Log, Topic = "legacy-lease" });
        int received = 0;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await bus.SubscribeAsync<LegacyEvent>(async (_, _) =>
        {
            Interlocked.Increment(ref received);
            await Task.Delay(TimeSpan.FromSeconds(2.5), CancellationToken.None);
            done.TrySetResult();
        }, TestCancellationToken);

        await bus.PublishAsync(new LegacyEvent(1), cancellationToken: TestCancellationToken);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10), TestCancellationToken);

        Assert.True(await _transport.WaitUntilIdleAsync(TimeSpan.FromSeconds(10), TestCancellationToken));
        Assert.Equal(1, Volatile.Read(ref received));
    }

    [Fact]
    public async Task PublishAsync_WithADelayWithinThePublishLimit_LetsTheTransportHoldIt()
    {
        var sent = new ConcurrentQueue<OutgoingMessage>();
        var transport = new InterceptingMessageTransport(_transport, _transport.Capabilities with { MaxDelay = TimeSpan.FromMilliseconds(100), MaxPublishDelay = null })
        {
            OnSend = (_, messages) =>
            {
                foreach (var message in messages)
                    sent.Enqueue(message);
            }
        };
        await using var bus = new LegacyMessageBusAdapter(transport, new SharedMessageBusOptions { LoggerFactory = Log, Topic = "legacy-delay" });

        await bus.PublishAsync(new LegacyEvent(1), new MessageOptions { DeliveryDelay = TimeSpan.FromSeconds(30) }, TestCancellationToken);

        Assert.NotNull(Assert.Single(sent).DeliverAt);
    }

    [Fact]
    public async Task Receive_WithManyUnknownTypeNames_BoundsTheTypeCache()
    {
        await using var bus = new LegacyMessageBusAdapter(_transport, new SharedMessageBusOptions { LoggerFactory = Log, Topic = "legacy-types" });
        int received = 0;
        await bus.SubscribeAsync<IMessage>(_ => Interlocked.Increment(ref received), TestCancellationToken);

        const int count = 1500;
        await _transport.SendAsync(MessageDestination.Topic("legacy-types"), Enumerable.Range(0, count).Select(i => new OutgoingMessage
        {
            MessageId = Guid.NewGuid().ToString("N"),
            Body = Encoding.UTF8.GetBytes("{}"),
            Headers = new Dictionary<string, string> { ["legacy-type"] = $"Unknown.Type{i}, Unknown.Assembly" }
        }).ToList(), TestCancellationToken);

        await WaitForAsync(() => Volatile.Read(ref received) == count);
        Assert.InRange(bus.CachedTypeCount, 0, 1000);
    }

    public override async ValueTask DisposeAsync()
    {
        await _transport.DisposeAsync();
        await base.DisposeAsync();
    }

    private async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The condition was not met in time.");

            await Task.Delay(20, TestCancellationToken);
        }
    }

    public record LegacyEvent(int Id);
}
