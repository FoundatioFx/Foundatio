using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.AsyncEx;
using Foundatio.Messaging;
using Foundatio.Tests.Extensions;
using Foundatio.Tests.Messaging;
using Foundatio.Xunit;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Foundatio.RabbitMQ.Tests.Messaging;

public class RabbitMqScalingTests(AspireFixture fixture, ITestOutputHelper output)
    : TestWithLoggingBase(output), IClassFixture<AspireFixture>
{
    private ChaosTestHelper? _chaos;
    private ChaosTestHelper Chaos => _chaos ??= new(fixture.App, Log);

    [Fact]
    public async Task SubscribeAsync_WithCompetingConsumers_DistributesMessagesAcrossAll()
    {
        Assert.SkipWhen(!fixture.IsAvailable, "RabbitMQ infrastructure not available");

        // Arrange
        string topic = "scaling-competing-" + Guid.NewGuid().ToString("N")[..8];
        string queueName = $"{topic}-shared";
        const int messageCount = 50;
        const int consumerCount = 3;

        var received = new ConcurrentDictionary<int, ConcurrentBag<string>>();
        for (int i = 0; i < consumerCount; i++)
            received[i] = [];

        var buses = new RabbitMQMessageBus[consumerCount];
        var allReceived = new AsyncCountdownEvent(messageCount);

        try
        {
            for (int i = 0; i < consumerCount; i++)
            {
                int consumerIndex = i;
                buses[i] = new RabbitMQMessageBus(o => o
                    .ConnectionString(fixture.MessagingConnectionString!)
                    .Topic(topic)
                    .SubscriptionQueueName(queueName)
                    .IsSubscriptionQueueExclusive(false)
                    .SubscriptionQueueAutoDelete(false)
                    .AcknowledgementStrategy(AcknowledgementStrategy.Automatic)
                    .PrefetchCount(1)
                    .UseQuorumQueues()
                    .LoggerFactory(Log));

                await buses[i].SubscribeAsync<SimpleMessageA>(msg =>
                {
                    received[consumerIndex].Add(msg.Data!);
                    allReceived.Signal();
                }, TestCancellationToken);
            }

            await Task.Delay(TimeSpan.FromSeconds(2), TestCancellationToken);

            // Act
            await using var publisher = new RabbitMQMessageBus(o => o
                .ConnectionString(fixture.MessagingConnectionString!)
                .Topic(topic)
                .LoggerFactory(Log));

            for (int i = 0; i < messageCount; i++)
            {
                await publisher.PublishAsync(new SimpleMessageA { Data = $"msg-{i}" },
                    cancellationToken: TestCancellationToken);
            }

            await allReceived.WaitAsync(TimeSpan.FromSeconds(30));

            // Assert
            int totalReceived = received.Values.Sum(b => b.Count);
            Assert.Equal(messageCount, totalReceived);

            foreach (var (consumerIndex, bag) in received)
            {
                _logger.LogInformation("Consumer {Index} received {Count} messages", consumerIndex, bag.Count);
                Assert.True(bag.Count > 0, $"Consumer {consumerIndex} should have received at least 1 message (got 0 of {messageCount})");
            }
        }
        finally
        {
            foreach (var bus in buses.OfType<RabbitMQMessageBus>())
                await bus.DisposeAsync();
        }
    }

    [Fact]
    public async Task SubscribeAsync_WithPrefetchLimit_OnlyDeliversUpToPrefetchCount()
    {
        Assert.SkipWhen(!fixture.IsAvailable, "RabbitMQ infrastructure not available");

        // Arrange
        string topic = "scaling-prefetch-" + Guid.NewGuid().ToString("N")[..8];
        string queueName = $"{topic}-prefetch";
        const ushort prefetchCount = 2;
        const int messageCount = 10;

        var deliveredBeforeAck = new ConcurrentBag<string>();
        var releaseGate = new AsyncManualResetEvent(false);

        await using var messageBus = new RabbitMQMessageBus(o => o
            .ConnectionString(fixture.MessagingConnectionString!)
            .Topic(topic)
            .SubscriptionQueueName(queueName)
            .IsSubscriptionQueueExclusive(false)
            .SubscriptionQueueAutoDelete(false)
            .AcknowledgementStrategy(AcknowledgementStrategy.Automatic)
            .PrefetchCount(prefetchCount)
            .UseQuorumQueues()
            .LoggerFactory(Log));

        try
        {
            await messageBus.SubscribeAsync<SimpleMessageA>(async msg =>
            {
                deliveredBeforeAck.Add(msg.Data!);
                _logger.LogInformation("Received message: {Data} (total delivered: {Count})", msg.Data, deliveredBeforeAck.Count);
                await releaseGate.WaitAsync(TestCancellationToken);
            }, TestCancellationToken);

            await Task.Delay(TimeSpan.FromSeconds(1), TestCancellationToken);

            // Act
            await using var publisher = new RabbitMQMessageBus(o => o
                .ConnectionString(fixture.MessagingConnectionString!)
                .Topic(topic)
                .LoggerFactory(Log));

            for (int i = 0; i < messageCount; i++)
            {
                await publisher.PublishAsync(new SimpleMessageA { Data = $"prefetch-{i}" },
                    cancellationToken: TestCancellationToken);
            }

            await Task.Delay(TimeSpan.FromSeconds(5), TestCancellationToken);

            // Assert
            int deliveredWhileBlocked = deliveredBeforeAck.Count;
            _logger.LogInformation("Messages delivered while consumer is blocked: {Count} (prefetch={Prefetch})",
                deliveredWhileBlocked, prefetchCount);

            Assert.True(deliveredWhileBlocked <= prefetchCount,
                $"Expected at most {prefetchCount} messages delivered while consumer is blocked, but got {deliveredWhileBlocked}");
        }
        finally
        {
            releaseGate.Set();
        }
    }

    [Fact]
    public async Task PublishAsync_WithConfirmsEnabled_GuaranteesDeliveryToSubscriber()
    {
        Assert.SkipWhen(!fixture.IsAvailable, "RabbitMQ infrastructure not available");

        // Arrange
        string topic = "scaling-confirms-" + Guid.NewGuid().ToString("N")[..8];
        string queueName = $"{topic}-confirmed";
        var received = new ConcurrentBag<string>();
        var messageReceived = new AsyncCountdownEvent(1);

        await using var subscriber = new RabbitMQMessageBus(o => o
            .ConnectionString(fixture.MessagingConnectionString!)
            .Topic(topic)
            .SubscriptionQueueName(queueName)
            .IsSubscriptionQueueExclusive(false)
            .SubscriptionQueueAutoDelete(false)
            .AcknowledgementStrategy(AcknowledgementStrategy.Automatic)
            .UseQuorumQueues()
            .LoggerFactory(Log));

        await subscriber.SubscribeAsync<SimpleMessageA>(msg =>
        {
            received.Add(msg.Data!);
            messageReceived.Signal();
        }, TestCancellationToken);

        await Task.Delay(TimeSpan.FromSeconds(1), TestCancellationToken);

        await using var publisher = new RabbitMQMessageBus(o => o
            .ConnectionString(fixture.MessagingConnectionString!)
            .Topic(topic)
            .PublisherConfirmsEnabled(true)
            .LoggerFactory(Log));

        // Act
        await publisher.PublishAsync(new SimpleMessageA { Data = "confirmed-message" },
            cancellationToken: TestCancellationToken);

        // Assert
        await messageReceived.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("confirmed-message", received);
    }

    [Fact]
    public async Task SubscribeAsync_WithMismatchedQueueArguments_ThrowsWithoutRetry()
    {
        Assert.SkipWhen(!fixture.IsAvailable, "RabbitMQ infrastructure not available");

        // Arrange
        string topic = "scaling-mismatch-" + Guid.NewGuid().ToString("N")[..8];
        string queueName = $"{topic}-mismatch";

        await using var classicBus = new RabbitMQMessageBus(o => o
            .ConnectionString(fixture.MessagingConnectionString!)
            .Topic(topic)
            .SubscriptionQueueName(queueName)
            .IsSubscriptionQueueExclusive(false)
            .SubscriptionQueueAutoDelete(false)
            .IsDurable(true)
            .LoggerFactory(Log));

        await classicBus.SubscribeAsync<SimpleMessageA>(_ => { }, TestCancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(2), TestCancellationToken);

        // Act
        var exception = await Record.ExceptionAsync(async () =>
        {
            await using var quorumBus = new RabbitMQMessageBus(o => o
                .ConnectionString(fixture.MessagingConnectionString!)
                .Topic(topic)
                .SubscriptionQueueName(queueName)
                .IsSubscriptionQueueExclusive(false)
                .SubscriptionQueueAutoDelete(false)
                .IsDurable(true)
                .UseQuorumQueues()
                .LoggerFactory(Log));

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await quorumBus.SubscribeAsync<SimpleMessageA>(_ => { }, cts.Token);
        });

        // Assert
        _logger.LogInformation("Queue mismatch exception: {Type}: {Message}",
            exception?.GetType().Name, exception?.Message);

        Assert.NotNull(exception);
    }

    [Fact]
    public async Task SubscribeAsync_AfterMemoryAlarm_ResumesReceivingMessages()
    {
        Assert.SkipWhen(!fixture.ChaosClusterAvailable, "Chaos cluster not available");

        // Arrange
        var connectionString = Chaos.GetConnectionString("chaos-1");
        var received = new ConcurrentBag<string>();

        await using var messageBus = new RabbitMQMessageBus(o => o
            .ConnectionString(connectionString)
            .Topic("scaling-memory-alarm-" + Guid.NewGuid().ToString("N")[..8])
            .LoggerFactory(Log));

        await messageBus.SubscribeAsync<SimpleMessageA>(msg =>
        {
            received.Add(msg.Data!);
        }, TestCancellationToken);

        await messageBus.PublishAsync(new SimpleMessageA { Data = "before-memory-alarm" },
            cancellationToken: TestCancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(1), TestCancellationToken);
        Assert.Contains("before-memory-alarm", received);

        // Act
        try
        {
            await Chaos.TriggerMemoryAlarmAsync("chaos-1", TestCancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(3), TestCancellationToken);

            await Chaos.ClearMemoryAlarmAsync("chaos-1", TestCancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(3), TestCancellationToken);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            bool published = false;
            while (!cts.Token.IsCancellationRequested && !published)
            {
                try
                {
                    await messageBus.PublishAsync(new SimpleMessageA { Data = "after-memory-alarm" },
                        cancellationToken: cts.Token);
                    published = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException and not StackOverflowException)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cts.Token);
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(2), TestCancellationToken);

            // Assert
            Assert.Contains("after-memory-alarm", received);
        }
        finally
        {
            await Chaos.ClearMemoryAlarmAsync("chaos-1", TestCancellationToken);
        }
    }

    [Fact]
    public async Task SubscribeAsync_AfterConnectionForceClose_ReconnectsAndResumes()
    {
        Assert.SkipWhen(!fixture.ChaosClusterAvailable, "Chaos cluster not available");

        // Arrange
        var connectionString = Chaos.GetConnectionString("chaos-2");
        var received = new ConcurrentBag<string>();

        await using var messageBus = new RabbitMQMessageBus(o => o
            .ConnectionString(connectionString)
            .Topic("scaling-force-close-" + Guid.NewGuid().ToString("N")[..8])
            .LoggerFactory(Log));

        await messageBus.SubscribeAsync<SimpleMessageA>(msg =>
        {
            received.Add(msg.Data!);
        }, TestCancellationToken);

        await messageBus.PublishAsync(new SimpleMessageA { Data = "before-force-close" },
            cancellationToken: TestCancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(1), TestCancellationToken);
        Assert.Contains("before-force-close", received);

        // Act
        await Chaos.CloseAllConnectionsAsync("chaos-2", TestCancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(5), TestCancellationToken);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        bool messageReceived = false;

        while (!cts.Token.IsCancellationRequested && !messageReceived)
        {
            try
            {
                await messageBus.PublishAsync(new SimpleMessageA { Data = "after-force-close" },
                    cancellationToken: cts.Token);
                await Task.Delay(TimeSpan.FromSeconds(1), cts.Token);
                messageReceived = received.Contains("after-force-close");
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException and not StackOverflowException)
            {
                _logger.LogWarning(ex, "Still recovering from force-close...");
                await Task.Delay(TimeSpan.FromSeconds(1), cts.Token);
            }
        }

        // Assert
        Assert.True(messageReceived, "Subscriber should receive messages after forced connection close");
    }

    [Fact]
    public async Task PublishAsync_DuringRollingNodeRestart_MaintainsDeliveryWithQuorumQueues()
    {
        Assert.SkipWhen(!fixture.ChaosClusterAvailable, "Chaos cluster not available");

        // Arrange
        var host1 = Chaos.GetConnectionString("chaos-1");
        var host2 = Chaos.GetConnectionString("chaos-2");
        var host3 = Chaos.GetConnectionString("chaos-3");
        var uri1 = new Uri(host1);
        var uri2 = new Uri(host2);
        var uri3 = new Uri(host3);

        string topic = "scaling-rolling-" + Guid.NewGuid().ToString("N")[..8];
        string queueName = $"{topic}-rolling";
        var published = new ConcurrentBag<string>();
        var received = new ConcurrentBag<string>();

        await using var publisher = new RabbitMQMessageBus(o => o
            .ConnectionString(host1)
            .Hosts($"{uri1.Host}:{uri1.Port}", $"{uri2.Host}:{uri2.Port}", $"{uri3.Host}:{uri3.Port}")
            .Topic(topic)
            .PublisherConfirmsEnabled(true)
            .PublishRecoveryTimeout(TimeSpan.FromSeconds(30))
            .LoggerFactory(Log));

        await using var subscriber = new RabbitMQMessageBus(o => o
            .ConnectionString(host1)
            .Hosts($"{uri1.Host}:{uri1.Port}", $"{uri2.Host}:{uri2.Port}", $"{uri3.Host}:{uri3.Port}")
            .Topic(topic)
            .SubscriptionQueueName(queueName)
            .IsSubscriptionQueueExclusive(false)
            .SubscriptionQueueAutoDelete(false)
            .AcknowledgementStrategy(AcknowledgementStrategy.Automatic)
            .PrefetchCount(10)
            .UseQuorumQueues()
            .LoggerFactory(Log));

        await subscriber.SubscribeAsync<SimpleMessageA>(msg =>
        {
            received.Add(msg.Data!);
        }, TestCancellationToken);

        await Task.Delay(TimeSpan.FromSeconds(1), TestCancellationToken);

        await publisher.PublishAsync(new SimpleMessageA { Data = "warmup" },
            cancellationToken: TestCancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(1), TestCancellationToken);

        // Act
        using var publishCts = new CancellationTokenSource();
        int publishCount = 0;

        var publishTask = Task.Run(async () =>
        {
            while (!publishCts.Token.IsCancellationRequested)
            {
                try
                {
                    var msg = $"rolling-{Interlocked.Increment(ref publishCount)}";
                    await publisher.PublishAsync(new SimpleMessageA { Data = msg },
                        cancellationToken: publishCts.Token);
                    published.Add(msg);
                    await Task.Delay(TimeSpan.FromMilliseconds(500), publishCts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Publish failed during rolling restart, retrying...");
                    await Task.Delay(TimeSpan.FromSeconds(1), publishCts.Token);
                }
            }
        }, publishCts.Token);

        await Task.Delay(TimeSpan.FromSeconds(2), TestCancellationToken);

        string[] nodeOrder = ["chaos-1", "chaos-2", "chaos-3"];
        foreach (string node in nodeOrder)
        {
            _logger.LogInformation("Rolling restart: stopping {Node}", node);
            await Chaos.StopNodeAsync(node, TestCancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(5), TestCancellationToken);

            _logger.LogInformation("Rolling restart: starting {Node}", node);
            await Chaos.StartNodeAsync(node, TestCancellationToken);
            await Chaos.WaitForNodeReadyAsync(node, TimeSpan.FromSeconds(30), TestCancellationToken);
        }

        await Task.Delay(TimeSpan.FromSeconds(5), TestCancellationToken);
        await publishCts.CancelAsync();

        try { await publishTask; }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Publish task cancelled during shutdown (expected)");
        }

        await Task.Delay(TimeSpan.FromSeconds(3), TestCancellationToken);

        // Assert
        _logger.LogInformation("Rolling restart results: published={Published}, received={Received}",
            published.Count, received.Count);

        Assert.True(published.Count > 0, "Should have published at least some messages during rolling restart");
        Assert.True(received.Count > 0, "Should have received messages during rolling restart");

        int receivedRolling = received.Count(m => m.StartsWith("rolling-"));
        double lossRate = published.Count > 0
            ? Math.Max(0.0, 1.0 - ((double)receivedRolling / published.Count))
            : 0.0;
        _logger.LogInformation("Message loss rate: {LossRate:P2} (published={Pub}, received rolling={Recv})",
            lossRate, published.Count, receivedRolling);
        Assert.True(lossRate < 0.1, $"Message loss rate should be under 10% with quorum queues, was {lossRate:P2}");
    }

    [Fact]
    public async Task SubscribeAsync_AfterConsumerDisconnectWithUnackedMessages_RedeliversToNewConsumer()
    {
        Assert.SkipWhen(!fixture.ChaosClusterAvailable, "Chaos cluster not available");

        // Arrange
        var host1 = Chaos.GetConnectionString("chaos-1");
        var host2 = Chaos.GetConnectionString("chaos-2");
        var host3 = Chaos.GetConnectionString("chaos-3");
        var uri1 = new Uri(host1);
        var uri2 = new Uri(host2);
        var uri3 = new Uri(host3);

        string topic = "scaling-inflight-" + Guid.NewGuid().ToString("N")[..8];
        string queueName = $"{topic}-inflight";
        var firstDeliveries = new ConcurrentBag<string>();
        var redeliveries = new ConcurrentBag<string>();
        var holdGate = new AsyncManualResetEvent(false);
        var allHosts = new[] { $"{uri1.Host}:{uri1.Port}", $"{uri2.Host}:{uri2.Port}", $"{uri3.Host}:{uri3.Port}" };

        await using var publisher = new RabbitMQMessageBus(o => o
            .ConnectionString(host1)
            .Hosts(allHosts)
            .Topic(topic)
            .PublisherConfirmsEnabled(true)
            .LoggerFactory(Log));

        RabbitMQMessageBus? subscriber1 = new RabbitMQMessageBus(o => o
            .ConnectionString(host3)
            .Hosts(allHosts)
            .Topic(topic)
            .SubscriptionQueueName(queueName)
            .IsSubscriptionQueueExclusive(false)
            .SubscriptionQueueAutoDelete(false)
            .AcknowledgementStrategy(AcknowledgementStrategy.Automatic)
            .PrefetchCount(5)
            .UseQuorumQueues()
            .DeliveryLimit(5)
            .LoggerFactory(Log));

        try
        {
            await subscriber1.SubscribeAsync<SimpleMessageA>(async msg =>
            {
                _logger.LogInformation("Subscriber1 received: {Data}", msg.Data);
                firstDeliveries.Add(msg.Data!);
                await holdGate.WaitAsync(TestCancellationToken);
            }, TestCancellationToken);

            await Task.Delay(TimeSpan.FromSeconds(1), TestCancellationToken);

            for (int i = 0; i < 3; i++)
            {
                await publisher.PublishAsync(new SimpleMessageA { Data = $"inflight-{i}" },
                    cancellationToken: TestCancellationToken);
            }

            await Task.Delay(TimeSpan.FromSeconds(2), TestCancellationToken);
            _logger.LogInformation("Messages delivered to subscriber1 before kill: {Count}", firstDeliveries.Count);

            // Act
            await subscriber1.DisposeAsync();
            subscriber1 = null;

            await using var subscriber2 = new RabbitMQMessageBus(o => o
                .ConnectionString(host1)
                .Hosts(allHosts)
                .Topic(topic)
                .SubscriptionQueueName(queueName)
                .IsSubscriptionQueueExclusive(false)
                .SubscriptionQueueAutoDelete(false)
                .AcknowledgementStrategy(AcknowledgementStrategy.Automatic)
                .PrefetchCount(5)
                .UseQuorumQueues()
                .DeliveryLimit(5)
                .LoggerFactory(Log));

            await subscriber2.SubscribeAsync<SimpleMessageA>(msg =>
            {
                _logger.LogInformation("Subscriber2 received (redelivery): {Data}", msg.Data);
                redeliveries.Add(msg.Data!);
            }, TestCancellationToken);

            await Task.Delay(TimeSpan.FromSeconds(5), TestCancellationToken);

            // Assert
            _logger.LogInformation("Redelivered messages: {Count}", redeliveries.Count);
            Assert.True(redeliveries.Count >= 1,
                $"Expected at least 1 redelivered message after subscriber disconnect, got {redeliveries.Count}");
        }
        finally
        {
            holdGate.Set();
            if (subscriber1 is not null)
                await subscriber1.DisposeAsync();
        }
    }
}
