using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.AsyncEx;
using Foundatio.Messaging;
using Foundatio.Tests.Extensions;
using Foundatio.Tests.Messaging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Foundatio.RabbitMQ.Tests.Messaging;

public abstract class RabbitMqMessageBusTestBase(string connectionString, ITestOutputHelper output) : MessageBusTestBase(output)
{
    private readonly string _topic = $"test_topic_{DateTime.UtcNow.Ticks}";
    protected readonly string ConnectionString = connectionString;

    protected override IMessageBus? GetMessageBus(Func<SharedMessageBusOptions, SharedMessageBusOptions>? config = null)
    {
        if (string.IsNullOrEmpty(ConnectionString))
            return null;

        return new RabbitMQMessageBus(o =>
        {
            o.SubscriptionQueueName($"{_topic}_{Guid.NewGuid():N}");
            o.ConnectionString(ConnectionString);
            o.UseQuorumQueues();
            o.LoggerFactory(Log);

            config?.Invoke(o.Target);

            return o;
        });
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
        // Fire and Forget is the default
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
    public virtual async Task CanHandlePoisonedMessageWithAutomaticAcknowledgementsAsync()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(ConnectionString), "RabbitMQ infrastructure not available");

        string topic = $"test_topic_poisoned_{DateTime.UtcNow.Ticks}";
        await using var messageBus = new RabbitMQMessageBus(o => o
            .ConnectionString(ConnectionString)
            .SubscriptionQueueName($"{topic}_{Guid.NewGuid():N}")
            .AcknowledgementStrategy(AcknowledgementStrategy.Automatic)
            .UseQuorumQueues()
            .LoggerFactory(Log));

        long handlerInvocations = 0;

        try
        {
            await messageBus.SubscribeAsync<SimpleMessageA>(_ =>
            {
                _logger.LogTrace("SimpleAMessage received");
                Interlocked.Increment(ref handlerInvocations);
                throw new Exception("Poisoned message");
            }, TestCancellationToken);

            await messageBus.PublishAsync(new SimpleMessageA(), cancellationToken: TestCancellationToken);
            _logger.LogTrace("Published one...");

            await Task.Delay(TimeSpan.FromSeconds(3), TestCancellationToken);
            Assert.Equal(3, handlerInvocations);
        }
        finally
        {
            await CleanupMessageBusAsync(messageBus);
        }
    }

    [Fact]
    public async Task PublishAsync_WithPriority_DeliversHighPriorityFirst()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(ConnectionString), "RabbitMQ infrastructure not available");

        // Arrange
        string topic = $"test_topic_priority_{DateTime.UtcNow.Ticks}";
        string queueName = $"{topic}_{Guid.NewGuid():N}";

        await using var publisher = new RabbitMQMessageBus(o => o
            .ConnectionString(ConnectionString)
            .SubscriptionQueueName(queueName)
            .AcknowledgementStrategy(AcknowledgementStrategy.Automatic)
            .UseQuorumQueues()
            .UseMessagePriority()
            .PrefetchCount(1)
            .LoggerFactory(Log));

        await publisher.PublishAsync(new SimpleMessageA { Data = "low" },
            new MessageOptions { Properties = { ["Priority"] = "1" } }, TestCancellationToken);
        await publisher.PublishAsync(new SimpleMessageA { Data = "high" },
            new MessageOptions { Properties = { ["Priority"] = "10" } }, TestCancellationToken);
        await publisher.PublishAsync(new SimpleMessageA { Data = "medium" },
            new MessageOptions { Properties = { ["Priority"] = "5" } }, TestCancellationToken);

        await Task.Delay(TimeSpan.FromMilliseconds(500), TestCancellationToken);

        var received = new ConcurrentQueue<string>();
        var countdownEvent = new AsyncCountdownEvent(3);

        // Act
        await publisher.SubscribeAsync<SimpleMessageA>(msg =>
        {
            received.Enqueue(msg.Data!);
            countdownEvent.Signal();
        }, TestCancellationToken);

        await countdownEvent.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        var messages = received.ToArray();
        Assert.Equal(3, messages.Length);
        Assert.Equal("high", messages[0]);
        Assert.Equal("medium", messages[1]);
        Assert.Equal("low", messages[2]);
    }

    [Fact]
    public async Task CanPersistAndNotLoseMessages()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(ConnectionString), "RabbitMQ infrastructure not available");

        var messageBus1 = new RabbitMQMessageBus(o => o
            .ConnectionString(ConnectionString)
            .LoggerFactory(Log)
            .SubscriptionQueueName($"{_topic}-offline")
            .IsSubscriptionQueueExclusive(false)
            .SubscriptionQueueAutoDelete(false)
            .AcknowledgementStrategy(AcknowledgementStrategy.Automatic));

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

        await messageBus1.DisposeAsync();

        var messageBus2 = new RabbitMQMessageBus(o => o
            .ConnectionString(ConnectionString)
            .LoggerFactory(Log)
            .SubscriptionQueueName($"{_topic}-offline")
            .IsSubscriptionQueueExclusive(false)
            .SubscriptionQueueAutoDelete(false)
            .AcknowledgementStrategy(AcknowledgementStrategy.Automatic));

        cts = new CancellationTokenSource();
        countdownEvent.AddCount(4);
        await messageBus2.SubscribeAsync<SimpleMessageA>(msg =>
        {
            _logger.LogInformation("[Subscriber3] Got message: {Message}", msg.Data);
            countdownEvent.Signal();
        }, cts.Token);
        await messageBus2.PublishAsync(new SimpleMessageA { Data = "Another audit message 4" }, cancellationToken: TestCancellationToken);
        await countdownEvent.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, countdownEvent.CurrentCount);

        await messageBus2.DisposeAsync();
    }
}
