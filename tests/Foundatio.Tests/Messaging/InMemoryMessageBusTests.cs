using System;
using System.Threading.Tasks;
using Foundatio.AsyncEx;
using Foundatio.Messaging;
using Foundatio.Tests.Extensions;
using Xunit;

namespace Foundatio.Tests.Messaging;

public class InMemoryMessageBusTests : MessageBusTestBase, IDisposable
{
    private IMessageBus? _messageBus;

    public InMemoryMessageBusTests(ITestOutputHelper output) : base(output) { }

    protected override IMessageBus GetMessageBus(Func<SharedMessageBusOptions, SharedMessageBusOptions>? config = null)
    {
        if (_messageBus != null)
            return _messageBus;

        _messageBus = new InMemoryMessageBus(o =>
        {
            o.LoggerFactory(Log);
            if (config != null)
                config(o.Target);

            return o;
        });
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
    public override Task SubscribeAsync_WithValidThenPoisonedMessage_DeliversOnlyValidMessageAsync()
    {
        return base.SubscribeAsync_WithValidThenPoisonedMessage_DeliversOnlyValidMessageAsync();
    }

    [Fact]
    public override Task PublishAsync_WithSerializationFailure_ThrowsSerializerExceptionAsync()
    {
        return base.PublishAsync_WithSerializationFailure_ThrowsSerializerExceptionAsync();
    }

    [Fact]
    public override Task SubscribeAsync_WithDeserializationFailure_SkipsMessageAsync()
    {
        return base.SubscribeAsync_WithDeserializationFailure_SkipsMessageAsync();
    }

    [Fact]
    public async Task CanCheckMessageCounts()
    {
        var messageBus = new InMemoryMessageBus(o => o.LoggerFactory(Log));
        await messageBus.PublishAsync(new SimpleMessageA
        {
            Data = "Hello"
        }, cancellationToken: TestCancellationToken);
        Assert.Equal(1, messageBus.MessagesSent);
        Assert.Equal(1, messageBus.GetMessagesSent<SimpleMessageA>());
        Assert.Equal(0, messageBus.GetMessagesSent<SimpleMessageB>());
    }

    [Fact]
    public async Task SendMessageToSubscribersAsync_WithNullMessageType_DeliversToRawSubscribersOnly()
    {
        // Arrange
        var messageBus = new TestableInMemoryMessageBus(o => o.LoggerFactory(Log));

        var rawReceived = new AsyncCountdownEvent(1);
        var typedReceived = new AsyncCountdownEvent(1);

        await messageBus.SubscribeAsync(msg =>
        {
            Assert.Null(msg.Type);
            Assert.Null(msg.ClrType);
            Assert.NotEmpty(msg.Data);
            rawReceived.Signal();
        }, TestCancellationToken);

        await messageBus.SubscribeAsync<SimpleMessageA>(_ =>
        {
            typedReceived.Signal();
        }, TestCancellationToken);

        var message = new Message("test payload"u8.ToArray(), _ => "test payload")
        {
            Type = null,
            ClrType = null
        };

        // Act
        await messageBus.TestSendMessageToSubscribersAsync(message);

        // Assert
        await rawReceived.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, rawReceived.CurrentCount);

        await Task.Delay(100, TestCancellationToken);
        Assert.Equal(1, typedReceived.CurrentCount);
    }

    public void Dispose()
    {
        _messageBus?.Dispose();
        _messageBus = null;
    }
}

internal class TestableInMemoryMessageBus : InMemoryMessageBus
{
    public TestableInMemoryMessageBus(Builder<InMemoryMessageBusOptionsBuilder, InMemoryMessageBusOptions> config)
        : base(config) { }

    public Task TestSendMessageToSubscribersAsync(IMessage message)
        => SendMessageToSubscribersAsync(message);
}
