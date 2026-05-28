using System;
using System.Threading.Tasks;
using Foundatio.AsyncEx;
using Foundatio.Messaging;
using Foundatio.Tests.Extensions;
using Foundatio.Tests.Messaging;
using Foundatio.Xunit;
using Xunit;

namespace Foundatio.RabbitMQ.Tests.Messaging;

public class RabbitMqVersionGatingTests(AspireFixture fixture, ITestOutputHelper output)
    : TestWithLoggingBase(output), IClassFixture<AspireFixture>
{
    [Fact]
    public async Task SubscribeAsync_WithDeprecatedGlobalQos_FallsBackToPerChannelQos()
    {
        Assert.SkipWhen(!fixture.IsAvailable, "RabbitMQ infrastructure not available");

        // Arrange
        string topic = "versiongate-globalqos-" + Guid.NewGuid().ToString("N")[..8];
        string queueName = $"{topic}-queue";
        var messageReceived = new AsyncCountdownEvent(1);
        string? receivedData = null;

#pragma warning disable CS0618
        await using var messageBus = new RabbitMQMessageBus(o => o
            .ConnectionString(fixture.MessagingConnectionString!)
            .Topic(topic)
            .SubscriptionQueueName(queueName)
            .PrefetchCount(10)
            .GlobalQos(true)
            .UseQuorumQueues()
            .LoggerFactory(Log));
#pragma warning restore CS0618

        await messageBus.SubscribeAsync<SimpleMessageA>(msg =>
        {
            receivedData = msg.Data;
            messageReceived.Signal();
        }, TestCancellationToken);

        await Task.Delay(TimeSpan.FromSeconds(1), TestCancellationToken);

        // Act
        await messageBus.PublishAsync(new SimpleMessageA { Data = "globalqos-fallback" },
            cancellationToken: TestCancellationToken);

        // Assert
        await messageReceived.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("globalqos-fallback", receivedData);
    }

    [Fact]
    public async Task PublishAsync_WithConfirmsAndVersionDetection_DeliversSuccessfully()
    {
        Assert.SkipWhen(!fixture.IsAvailable, "RabbitMQ infrastructure not available");

        // Arrange
        string topic = "versiongate-confirms-" + Guid.NewGuid().ToString("N")[..8];
        string queueName = $"{topic}-queue";
        var messageReceived = new AsyncCountdownEvent(1);
        string? receivedData = null;

        await using var messageBus = new RabbitMQMessageBus(o => o
            .ConnectionString(fixture.MessagingConnectionString!)
            .Topic(topic)
            .SubscriptionQueueName(queueName)
            .PublisherConfirmsEnabled(true)
            .UseQuorumQueues()
            .LoggerFactory(Log));

        await messageBus.SubscribeAsync<SimpleMessageA>(msg =>
        {
            receivedData = msg.Data;
            messageReceived.Signal();
        }, TestCancellationToken);

        await Task.Delay(TimeSpan.FromSeconds(1), TestCancellationToken);

        // Act
        await messageBus.PublishAsync(new SimpleMessageA { Data = "confirmed" },
            cancellationToken: TestCancellationToken);

        // Assert
        await messageReceived.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("confirmed", receivedData);
    }

    [Fact]
    public async Task SubscribeAsync_WithQuorumQueueAndDeliveryLimit_DeliversMessages()
    {
        Assert.SkipWhen(!fixture.IsAvailable, "RabbitMQ infrastructure not available");

        // Arrange
        string topic = "versiongate-delivery-" + Guid.NewGuid().ToString("N")[..8];
        string queueName = $"{topic}-queue";
        var messageReceived = new AsyncCountdownEvent(1);
        string? receivedData = null;

        await using var messageBus = new RabbitMQMessageBus(o => o
            .ConnectionString(fixture.MessagingConnectionString!)
            .Topic(topic)
            .SubscriptionQueueName(queueName)
            .UseQuorumQueues()
            .DeliveryLimit(3)
            .LoggerFactory(Log));

        await messageBus.SubscribeAsync<SimpleMessageA>(msg =>
        {
            receivedData = msg.Data;
            messageReceived.Signal();
        }, TestCancellationToken);

        await Task.Delay(TimeSpan.FromSeconds(1), TestCancellationToken);

        // Act
        await messageBus.PublishAsync(new SimpleMessageA { Data = "delivery-limit" },
            cancellationToken: TestCancellationToken);

        // Assert
        await messageReceived.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("delivery-limit", receivedData);
    }
}
