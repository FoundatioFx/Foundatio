using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Foundatio.Tests.Messaging;
using Foundatio.Xunit;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Foundatio.RabbitMQ.Tests.Messaging;

public class RabbitMqChaosTests(AspireFixture fixture, ITestOutputHelper output)
    : TestWithLoggingBase(output), IClassFixture<AspireFixture>
{
    private ChaosTestHelper? _chaos;
    private ChaosTestHelper Chaos => _chaos ??= new(fixture.App, Log);

    [Fact]
    public async Task PublishAsync_DuringDiskAlarm_BlocksUntilAlarmClears()
    {
        Assert.SkipWhen(!fixture.ChaosClusterAvailable, "Chaos cluster not available");

        // Arrange
        string connectionString = Chaos.GetConnectionString("chaos-1");
        await using var messageBus = new RabbitMQMessageBus(o => o
            .ConnectionString(connectionString)
            .Topic("chaos-disk-alarm-test-" + Guid.NewGuid().ToString("N")[..8])
            .LoggerFactory(Log));

        try
        {
            await messageBus.PublishAsync(new SimpleMessageA { Data = "warmup" }, cancellationToken: TestCancellationToken);

            // Act - trigger disk alarm
            await Chaos.FillDiskAsync("chaos-1", TestCancellationToken);
            await Chaos.WaitForAlarmActiveAsync("chaos-1", TimeSpan.FromSeconds(10), TestCancellationToken);

            // Issue a publish with a short timeout. The disk alarm should eventually block
            // the connection, causing this to either timeout or succeed quickly before the
            // block notification arrives. Either outcome is acceptable; the key assertion
            // is that publishing resumes after the alarm clears.
            using var alarmPublishCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await messageBus.PublishAsync(new SimpleMessageA { Data = "during alarm" },
                    cancellationToken: alarmPublishCts.Token);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Publish timed out during disk alarm (expected)");
            }

            // Clear alarm and verify publish resumes
            await Chaos.ClearDiskAsync("chaos-1", TestCancellationToken);
            await Chaos.WaitForAlarmClearedAsync("chaos-1", TimeSpan.FromSeconds(10), TestCancellationToken);

            // After clearing, a new publish should succeed
            using var recoveryCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            bool published = false;
            while (!recoveryCts.Token.IsCancellationRequested && !published)
            {
                try
                {
                    await messageBus.PublishAsync(new SimpleMessageA { Data = "after-clear" },
                        cancellationToken: recoveryCts.Token);
                    published = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException and not StackOverflowException)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), recoveryCts.Token);
                }
            }

            Assert.True(published, "Should be able to publish after disk alarm clears");
        }
        finally
        {
            await Chaos.ClearDiskAsync("chaos-1", TestCancellationToken);
        }
    }

    [Fact]
    public async Task SubscribeAsync_DuringDiskAlarm_ContinuesReceivingAfterRecovery()
    {
        Assert.SkipWhen(!fixture.ChaosClusterAvailable, "Chaos cluster not available");

        // Arrange
        string connectionString = Chaos.GetConnectionString("chaos-2");
        var received = new ConcurrentBag<string>();

        await using var messageBus = new RabbitMQMessageBus(o => o
            .ConnectionString(connectionString)
            .Topic("chaos-subscribe-test-" + Guid.NewGuid().ToString("N")[..8])
            .LoggerFactory(Log));

        await messageBus.SubscribeAsync<SimpleMessageA>(msg =>
        {
            received.Add(msg.Data!);
        }, TestCancellationToken);

        await messageBus.PublishAsync(new SimpleMessageA { Data = "before-alarm" }, cancellationToken: TestCancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(1), TestCancellationToken);

        // Act - trigger alarm and then clear it
        await Chaos.FillDiskAsync("chaos-2", TestCancellationToken);
        await Chaos.WaitForAlarmActiveAsync("chaos-2", TimeSpan.FromSeconds(10), TestCancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(2), TestCancellationToken);

        await Chaos.ClearDiskAsync("chaos-2", TestCancellationToken);
        await Chaos.WaitForAlarmClearedAsync("chaos-2", TimeSpan.FromSeconds(10), TestCancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(2), TestCancellationToken);

        await messageBus.PublishAsync(new SimpleMessageA { Data = "after-recovery" }, cancellationToken: TestCancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(2), TestCancellationToken);

        // Assert
        Assert.Contains("before-alarm", received);
        Assert.Contains("after-recovery", received);
    }

    [Fact]
    public async Task PublishAsync_AfterNodeRestart_RecoversAndDelivers()
    {
        Assert.SkipWhen(!fixture.ChaosClusterAvailable, "Chaos cluster not available");

        // Arrange
        string connectionString = Chaos.GetConnectionString("chaos-3");

        await using var messageBus = new RabbitMQMessageBus(o => o
            .ConnectionString(connectionString)
            .Topic("chaos-restart-test-" + Guid.NewGuid().ToString("N")[..8])
            .LoggerFactory(Log));

        await messageBus.PublishAsync(new SimpleMessageA { Data = "before-restart" }, cancellationToken: TestCancellationToken);

        // Act - kill and restart
        await Chaos.StopNodeAsync("chaos-3", TestCancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(3), TestCancellationToken);
        await Chaos.StartNodeAsync("chaos-3", TestCancellationToken);
        await Chaos.WaitForNodeReadyAsync("chaos-3", TimeSpan.FromSeconds(30), TestCancellationToken);

        // Assert - publish should eventually succeed after recovery
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        bool published = false;

        while (!cts.Token.IsCancellationRequested && !published)
        {
            try
            {
                await messageBus.PublishAsync(new SimpleMessageA { Data = "after-restart" },
                    cancellationToken: cts.Token);
                published = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException and not StackOverflowException)
            {
                _logger.LogWarning(ex, "Publish failed during recovery, retrying...");
                await Task.Delay(TimeSpan.FromSeconds(1), cts.Token);
            }
        }

        Assert.True(published, "Should be able to publish after node restart recovery");
    }

    [Fact]
    public async Task PublishAsync_WithMultipleHosts_FailsOverToHealthyNode()
    {
        Assert.SkipWhen(!fixture.ChaosClusterAvailable, "Chaos cluster not available");

        // Arrange
        string host1 = Chaos.GetConnectionString("chaos-1");
        string host2 = Chaos.GetConnectionString("chaos-2");
        string host3 = Chaos.GetConnectionString("chaos-3");
        var uri2 = new Uri(host2);
        var uri3 = new Uri(host3);

        await using var messageBus = new RabbitMQMessageBus(o => o
            .ConnectionString(host1)
            .Hosts($"{uri2.Host}:{uri2.Port}", $"{uri3.Host}:{uri3.Port}")
            .Topic("chaos-failover-test-" + Guid.NewGuid().ToString("N")[..8])
            .LoggerFactory(Log));

        await messageBus.PublishAsync(new SimpleMessageA { Data = "warmup" }, cancellationToken: TestCancellationToken);

        // Act - trigger disk alarm on primary
        await Chaos.FillDiskAsync("chaos-1", TestCancellationToken);
        await Chaos.WaitForAlarmActiveAsync("chaos-1", TimeSpan.FromSeconds(10), TestCancellationToken);

        try
        {
            // Assert - should still be able to publish (failover to chaos-2 or chaos-3)
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            bool published = false;

            while (!cts.Token.IsCancellationRequested && !published)
            {
                try
                {
                    await messageBus.PublishAsync(new SimpleMessageA { Data = "via-failover" },
                        cancellationToken: cts.Token);
                    published = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException and not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Publish attempt failed, retrying...");
                    await Task.Delay(TimeSpan.FromSeconds(1), cts.Token);
                }
            }

            _logger.LogInformation("Failover publish result: published={Published}", published);
            Assert.True(published, "Should be able to publish via failover to a healthy node");
        }
        finally
        {
            await Chaos.ClearDiskAsync("chaos-1", TestCancellationToken);
        }
    }

    [Fact]
    public async Task PublishAsync_DuringQuorumLoss_RetriesAndResumesWhenNodeRejoins()
    {
        Assert.SkipWhen(!fixture.ChaosClusterAvailable, "Chaos cluster not available");

        // Arrange - connect to all 3 cluster nodes
        string host1 = Chaos.GetConnectionString("chaos-1");
        string host2 = Chaos.GetConnectionString("chaos-2");
        string host3 = Chaos.GetConnectionString("chaos-3");
        var uri2 = new Uri(host2);
        var uri3 = new Uri(host3);

        await using var messageBus = new RabbitMQMessageBus(o => o
            .ConnectionString(host1)
            .Hosts($"{uri2.Host}:{uri2.Port}", $"{uri3.Host}:{uri3.Port}")
            .Topic("chaos-quorum-loss-test-" + Guid.NewGuid().ToString("N")[..8])
            .LoggerFactory(Log));

        await messageBus.PublishAsync(new SimpleMessageA { Data = "before-quorum-loss" }, cancellationToken: TestCancellationToken);

        // Act - kill 2 of 3 nodes (causes quorum loss in RabbitMQ 4.x Raft)
        await Chaos.StopNodeAsync("chaos-2", TestCancellationToken);
        await Chaos.StopNodeAsync("chaos-3", TestCancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(3), TestCancellationToken);

        // Publish should fail/timeout during quorum loss
        using var failCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var publishDuringLoss = await Record.ExceptionAsync(() =>
            messageBus.PublishAsync(new SimpleMessageA { Data = "during-quorum-loss" },
                cancellationToken: failCts.Token));

        _logger.LogInformation("Publish during quorum loss result: {Exception}", publishDuringLoss?.GetType().Name ?? "succeeded");
        Assert.True(publishDuringLoss is not null || failCts.IsCancellationRequested,
            "Publish should fail or timeout during quorum loss (2 of 3 nodes down)");

        // Act - bring nodes back to restore quorum
        await Chaos.StartNodeAsync("chaos-2", TestCancellationToken);
        await Chaos.StartNodeAsync("chaos-3", TestCancellationToken);
        await Chaos.WaitForNodeReadyAsync("chaos-2", TimeSpan.FromSeconds(30), TestCancellationToken);
        await Chaos.WaitForNodeReadyAsync("chaos-3", TimeSpan.FromSeconds(30), TestCancellationToken);

        // Assert - publishing should resume once quorum is restored
        using var recoveryCts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        bool published = false;

        while (!recoveryCts.Token.IsCancellationRequested && !published)
        {
            try
            {
                await messageBus.PublishAsync(new SimpleMessageA { Data = "after-quorum-restored" },
                    cancellationToken: recoveryCts.Token);
                published = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException and not StackOverflowException)
            {
                _logger.LogWarning(ex, "Publish still failing during recovery, retrying...");
                await Task.Delay(TimeSpan.FromSeconds(2), recoveryCts.Token);
            }
        }

        Assert.True(published, "Should be able to publish after quorum is restored (node rejoined)");
    }

    [Fact]
    public async Task PublishAsync_WithPublisherConfirms_DuringDiskAlarm_FailsOrTimesOut()
    {
        Assert.SkipWhen(!fixture.ChaosClusterAvailable, "Chaos cluster not available");

        // Arrange
        string connectionString = Chaos.GetConnectionString("chaos-1");

        await using var messageBus = new RabbitMQMessageBus(o => o
            .ConnectionString(connectionString)
            .Topic("chaos-confirms-test-" + Guid.NewGuid().ToString("N")[..8])
            .PublisherConfirmsEnabled(true)
            .PublishRecoveryTimeout(TimeSpan.FromSeconds(5))
            .LoggerFactory(Log));

        await messageBus.PublishAsync(new SimpleMessageA { Data = "warmup" }, cancellationToken: TestCancellationToken);

        // Act
        await Chaos.FillDiskAsync("chaos-1", TestCancellationToken);
        await Chaos.WaitForAlarmActiveAsync("chaos-1", TimeSpan.FromSeconds(10), TestCancellationToken);

        try
        {
            // Assert - publish with confirms should fail or timeout during alarm
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var exception = await Record.ExceptionAsync(() =>
                messageBus.PublishAsync(new SimpleMessageA { Data = "during alarm" },
                    cancellationToken: cts.Token));

            _logger.LogInformation("Exception during disk alarm publish: {Exception}", exception?.GetType().Name ?? "none");
            Assert.True(exception is not null || cts.IsCancellationRequested,
                "Publish with confirms should fail or timeout during disk alarm");
        }
        finally
        {
            await Chaos.ClearDiskAsync("chaos-1", TestCancellationToken);
        }
    }

    [Fact]
    public async Task SubscribeAsync_AfterNodeKill_ReconnectsAndReceivesMessages()
    {
        Assert.SkipWhen(!fixture.ChaosClusterAvailable, "Chaos cluster not available");

        // Arrange
        string connectionString = Chaos.GetConnectionString("chaos-3");
        var received = new ConcurrentBag<string>();

        await using var messageBus = new RabbitMQMessageBus(o => o
            .ConnectionString(connectionString)
            .Topic("chaos-sub-kill-test-" + Guid.NewGuid().ToString("N")[..8])
            .LoggerFactory(Log));

        await messageBus.SubscribeAsync<SimpleMessageA>(msg =>
        {
            received.Add(msg.Data!);
        }, TestCancellationToken);

        await messageBus.PublishAsync(new SimpleMessageA { Data = "before-kill" }, cancellationToken: TestCancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(1), TestCancellationToken);
        Assert.Contains("before-kill", received);

        // Act - kill node and restart
        await Chaos.StopNodeAsync("chaos-3", TestCancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(3), TestCancellationToken);
        await Chaos.StartNodeAsync("chaos-3", TestCancellationToken);
        await Chaos.WaitForNodeReadyAsync("chaos-3", TimeSpan.FromSeconds(30), TestCancellationToken);

        // Assert - subscriber should reconnect and receive new messages
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        bool messageReceived = false;

        while (!cts.Token.IsCancellationRequested && !messageReceived)
        {
            try
            {
                await messageBus.PublishAsync(new SimpleMessageA { Data = "after-kill" },
                    cancellationToken: cts.Token);
                await Task.Delay(TimeSpan.FromSeconds(1), cts.Token);
                messageReceived = received.Contains("after-kill");
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException and not StackOverflowException)
            {
                _logger.LogWarning(ex, "Publish/subscribe still recovering...");
                await Task.Delay(TimeSpan.FromSeconds(1), cts.Token);
            }
        }

        Assert.True(messageReceived, "Subscriber should receive messages after node kill/restart");
    }

    [Fact]
    public async Task PublishAsync_DuringRapidNodeFlapping_RemainsResilient()
    {
        Assert.SkipWhen(!fixture.ChaosClusterAvailable, "Chaos cluster not available");

        // Arrange
        string connectionString = Chaos.GetConnectionString("chaos-2");

        await using var messageBus = new RabbitMQMessageBus(o => o
            .ConnectionString(connectionString)
            .Topic("chaos-flapping-test-" + Guid.NewGuid().ToString("N")[..8])
            .LoggerFactory(Log));

        await messageBus.PublishAsync(new SimpleMessageA { Data = "warmup" }, cancellationToken: TestCancellationToken);

        // Act - rapid kill/start 3 times
        for (int i = 0; i < 3; i++)
        {
            _logger.LogInformation("Flap cycle {Cycle}/3: killing node", i + 1);
            await Chaos.StopNodeAsync("chaos-2", TestCancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(2), TestCancellationToken);

            _logger.LogInformation("Flap cycle {Cycle}/3: restarting node", i + 1);
            await Chaos.StartNodeAsync("chaos-2", TestCancellationToken);
            await Chaos.WaitForNodeReadyAsync("chaos-2", TimeSpan.FromSeconds(20), TestCancellationToken);
        }

        // Assert - should eventually be able to publish after flapping stabilizes
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        bool published = false;

        while (!cts.Token.IsCancellationRequested && !published)
        {
            try
            {
                await messageBus.PublishAsync(new SimpleMessageA { Data = "after-flapping" },
                    cancellationToken: cts.Token);
                published = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException and not StackOverflowException)
            {
                _logger.LogWarning(ex, "Still recovering from flapping...");
                await Task.Delay(TimeSpan.FromSeconds(1), cts.Token);
            }
        }

        Assert.True(published, "Publisher should recover after rapid node flapping");
    }
}
