using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.AsyncEx;
using Foundatio.Messaging;
using Foundatio.Tests.Extensions;
using Foundatio.Tests.Messaging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Foundatio.Kafka.Tests.Messaging;

public class KafkaMessageBusTests : KafkaMessageBusTestBase
{
    public KafkaMessageBusTests(ITestOutputHelper output) : base(output) { }

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
    public override Task CanSendDelayedMessageAsync()
    {
        return base.CanSendDelayedMessageAsync();
    }

    [Fact]
    public override Task CanSendMappedMessageAsync()
    {
        return base.CanSendMappedMessageAsync();
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
    public override Task CanTolerateSubscriberFailureAsync()
    {
        return base.CanTolerateSubscriberFailureAsync();
    }

    [Fact]
    public override Task CanSendMessageToMultipleSubscribersAsync()
    {
        return base.CanSendMessageToMultipleSubscribersAsync();
    }

    [Fact]
    public override Task CanSubscribeConcurrentlyAsync()
    {
        return base.CanSubscribeConcurrentlyAsync();
    }

    [Fact]
    public override Task WillOnlyReceiveSubscribedMessageTypeAsync()
    {
        return base.WillOnlyReceiveSubscribedMessageTypeAsync();
    }

    [Fact]
    public override Task CanCancelSubscriptionAsync()
    {
        return base.CanCancelSubscriptionAsync();
    }

    [Fact(Skip = "Kafka is a durable log; pre-subscribe messages are retained. See CanPersistAndNotLoseMessages.")]
    public override Task WontKeepMessagesWithNoSubscribersAsync()
    {
        return base.WontKeepMessagesWithNoSubscribersAsync();
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
    public override Task PublishAsync_WithDeliveryDelayExtension_DelaysDeliveryAsync()
    {
        return base.PublishAsync_WithDeliveryDelayExtension_DelaysDeliveryAsync();
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
    public override Task PublishAsync_WithUniqueId_PropagatesUniqueIdToSubscriberAsync()
    {
        return base.PublishAsync_WithUniqueId_PropagatesUniqueIdToSubscriberAsync();
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
    public override Task SubscribeAsync_ToRawIMessage_CanAccessAllPropertiesAsync()
    {
        return base.SubscribeAsync_ToRawIMessage_CanAccessAllPropertiesAsync();
    }

    [Fact]
    public override Task SubscribeAsync_WithCancellation_ThrowsOperationCanceledExceptionAsync()
    {
        return base.SubscribeAsync_WithCancellation_ThrowsOperationCanceledExceptionAsync();
    }

    [Fact]
    public override Task SubscribeAsync_WithCancellationTokenHandler_ReceivesCancellationTokenAsync()
    {
        return base.SubscribeAsync_WithCancellationTokenHandler_ReceivesCancellationTokenAsync();
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
    public async Task CanPersistAndNotLoseMessages()
    {
        var messageBus1 = GetMessageBus();
        if (messageBus1 is null)
            return;

        var countdownEvent = new AsyncCountdownEvent(1);
        var cts = new CancellationTokenSource();
        await messageBus1.SubscribeAsync<SimpleMessageA>(msg =>
        {
            _logger.LogInformation("[Subscriber1] Got message: {Message}", msg.Data);
            countdownEvent.Signal();
        }, cts.Token);

        await messageBus1.PublishAsync(new SimpleMessageA { Data = "Audit message 1" }, cancellationToken: TestCancellationToken);
        await countdownEvent.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, countdownEvent.CurrentCount);
        await cts.CancelAsync();

        await messageBus1.PublishAsync(new SimpleMessageA { Data = "Audit message 2" }, cancellationToken: TestCancellationToken);

        cts = new CancellationTokenSource();
        countdownEvent.AddCount(1);
        await messageBus1.SubscribeAsync<SimpleMessageA>(msg =>
        {
            _logger.LogInformation("[Subscriber2] Got message: {Message}", msg.Data);
            countdownEvent.Signal();
        }, cts.Token);
        await countdownEvent.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, countdownEvent.CurrentCount);
        await cts.CancelAsync();

        await messageBus1.PublishAsync(new SimpleMessageA { Data = "Audit offline message 1" }, cancellationToken: TestCancellationToken);
        await messageBus1.PublishAsync(new SimpleMessageA { Data = "Audit offline message 2" }, cancellationToken: TestCancellationToken);
        await messageBus1.PublishAsync(new SimpleMessageA { Data = "Audit offline message 3" }, cancellationToken: TestCancellationToken);

        messageBus1.Dispose();

        using var messageBus2 = GetMessageBus();
        Assert.NotNull(messageBus2);

        cts = new CancellationTokenSource();
        countdownEvent.AddCount(4);
        await messageBus2.SubscribeAsync<SimpleMessageA>(msg =>
        {
            _logger.LogInformation("[Subscriber3] Got message: {Message}", msg.Data);
            countdownEvent.Signal();
        }, cts.Token);
        await messageBus2.PublishAsync(new SimpleMessageA { Data = "Another audit message 4" }, cancellationToken: TestCancellationToken);
        await countdownEvent.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, countdownEvent.CurrentCount);

        // Cleanup
        cts.Dispose();

        await CleanupMessageBusAsync(messageBus2);
    }

    [Fact]
    public async Task CanReceiveMessagesWithPartitionEofEnabled_WhenPartitionReachesEof_DoesNotLogErrorsAsync()
    {
        // Arrange
        using var messageBus = new KafkaMessageBus(o => o
            .BootstrapServers("127.0.0.1:9092")
            .Topic(Topic)
            .TopicReplicationFactor(1)
            .TopicNumberOfPartitions(1)
            .GroupId(GroupId)
            .AllowAutoCreateTopics(true)
            .EnablePartitionEof(true)
            .LoggerFactory(Log)
        );

        var countdownEvent = new AsyncCountdownEvent(1);
        await messageBus.SubscribeAsync<SimpleMessageA>(msg =>
        {
            _logger.LogInformation("Got message: {Message}", msg.Data);
            countdownEvent.Signal();
        }, TestCancellationToken);

        // Act
        await messageBus.PublishAsync(new SimpleMessageA { Data = "Hello" }, cancellationToken: TestCancellationToken);
        await countdownEvent.WaitAsync(TimeSpan.FromSeconds(5));

        // Give the consumer time to reach end of partition after the message is consumed
        await Task.Delay(TimeSpan.FromSeconds(1), TestCancellationToken);

        // Assert
        Assert.Equal(0, countdownEvent.CurrentCount);
        var errorEntries = Log.LogEntries.Where(e => e.LogLevel == LogLevel.Error).ToList();
        Assert.Empty(errorEntries);

        await CleanupMessageBusAsync(messageBus);
    }
}
