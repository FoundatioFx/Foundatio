using System;
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

    public override async ValueTask DisposeAsync()
    {
        await _transport.DisposeAsync();
        await base.DisposeAsync();
    }
}
