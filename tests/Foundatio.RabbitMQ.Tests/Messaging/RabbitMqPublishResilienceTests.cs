using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Foundatio.Tests.Messaging;
using Xunit;

namespace Foundatio.RabbitMQ.Tests.Messaging;

public class RabbitMqPublishResilienceTests(ITestOutputHelper output) : RabbitMqMessageBusTestBase("amqp://localhost:5672", output)
{
    [Fact]
    public async Task PublishAsync_WithRecoveryTimeoutDisabled_FailsImmediatelyOnConnectionDrop()
    {
        await using var messageBus = new RabbitMQMessageBus(o => o
            .ConnectionString(ConnectionString)
            .LoggerFactory(Log)
            .PublishRecoveryTimeout(TimeSpan.Zero));

        await messageBus.PublishAsync(new SimpleMessageA { Data = "warmup" }, cancellationToken: TestCancellationToken);

        await messageBus.SimulatePublisherConnectionShutdownAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ex = await Assert.ThrowsAsync<MessageBusException>(() =>
            messageBus.PublishAsync(new SimpleMessageA { Data = "should fail" },
                cancellationToken: cts.Token));

        Assert.Contains("publisher channel is closed", ex.Message);
    }

    [Fact]
    public async Task PublishAsync_DuringRecovery_WaitsAndSucceeds()
    {
        await using var messageBus = new RabbitMQMessageBus(o => o
            .ConnectionString(ConnectionString)
            .LoggerFactory(Log)
            .PublishRecoveryTimeout(TimeSpan.FromSeconds(15)));

        await messageBus.PublishAsync(new SimpleMessageA { Data = "warmup" }, cancellationToken: TestCancellationToken);

        messageBus.SimulatePublisherConnectionLost();

        using var publishCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var publishTask = messageBus.PublishAsync(new SimpleMessageA { Data = "during recovery" },
            cancellationToken: publishCts.Token);

        await Task.Delay(100, TestCancellationToken);
        Assert.False(publishTask.IsCompleted);

        messageBus.SimulatePublisherRecoverySucceeded();

        await publishTask;
    }

    [Fact]
    public async Task PublishAsync_RecoveryTimeout_ThrowsMessageBusException()
    {
        await using var messageBus = new RabbitMQMessageBus(o => o
            .ConnectionString(ConnectionString)
            .LoggerFactory(Log)
            .PublishRecoveryTimeout(TimeSpan.FromMilliseconds(500)));

        await messageBus.PublishAsync(new SimpleMessageA { Data = "warmup" }, cancellationToken: TestCancellationToken);

        messageBus.SimulatePublisherConnectionLost();

        var ex = await Assert.ThrowsAsync<MessageBusException>(() =>
            messageBus.PublishAsync(new SimpleMessageA { Data = "should timeout" },
                cancellationToken: TestCancellationToken));

        Assert.Contains("connection recovery did not complete within", ex.Message);
    }

    [Fact]
    public async Task PublishAsync_CancellationDuringRecovery_RespectsCancellation()
    {
        await using var messageBus = new RabbitMQMessageBus(o => o
            .ConnectionString(ConnectionString)
            .LoggerFactory(Log)
            .PublishRecoveryTimeout(TimeSpan.FromSeconds(30)));

        await messageBus.PublishAsync(new SimpleMessageA { Data = "warmup" }, cancellationToken: TestCancellationToken);

        messageBus.SimulatePublisherConnectionLost();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            messageBus.PublishAsync(new SimpleMessageA { Data = "should cancel" }, cancellationToken: cts.Token));
    }

    [Fact]
    public async Task PublishAsync_WhenConnectionHealthy_SucceedsImmediately()
    {
        await using var messageBus = new RabbitMQMessageBus(o => o
            .ConnectionString(ConnectionString)
            .LoggerFactory(Log)
            .PublishRecoveryTimeout(TimeSpan.FromSeconds(10)));

        var exception = await Record.ExceptionAsync(() =>
            messageBus.PublishAsync(new SimpleMessageA { Data = "should succeed immediately" },
                cancellationToken: TestCancellationToken));

        Assert.Null(exception);
    }

    [Fact]
    public async Task PublishAsync_DuringDisposal_FailsFastWithOperationCanceledException()
    {
        var messageBus = new RabbitMQMessageBus(o => o
            .ConnectionString(ConnectionString)
            .LoggerFactory(Log)
            .PublishRecoveryTimeout(TimeSpan.FromSeconds(30)));

        try
        {
            await messageBus.PublishAsync(new SimpleMessageA { Data = "warmup" }, cancellationToken: TestCancellationToken);

            messageBus.SimulatePublisherConnectionLost();

            using var publishCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var publishTask = messageBus.PublishAsync(new SimpleMessageA { Data = "should fail on disposal" },
                cancellationToken: publishCts.Token);

            await Task.Delay(50, TestCancellationToken);
            Assert.False(publishTask.IsCompleted);

            await messageBus.DisposeAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publishTask);
        }
        finally
        {
            await messageBus.DisposeAsync();
        }
    }

    [Fact]
    public async Task PublishAsync_RecoveryErrorDoesNotOpenGate_WaitsUntilTimeout()
    {
        await using var messageBus = new RabbitMQMessageBus(o => o
            .ConnectionString(ConnectionString)
            .LoggerFactory(Log)
            .PublishRecoveryTimeout(TimeSpan.FromMilliseconds(500)));

        await messageBus.PublishAsync(new SimpleMessageA { Data = "warmup" }, cancellationToken: TestCancellationToken);

        messageBus.SimulatePublisherConnectionLost();

        using var publishCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var publishTask = messageBus.PublishAsync(new SimpleMessageA { Data = "should stay blocked" },
            cancellationToken: publishCts.Token);

        await Task.Delay(50, TestCancellationToken);
        Assert.False(publishTask.IsCompleted);

        await messageBus.SimulatePublisherConnectionRecoveryErrorAsync();

        await Task.Delay(50, TestCancellationToken);
        Assert.False(publishTask.IsCompleted);

        var ex = await Assert.ThrowsAsync<MessageBusException>(() => publishTask);
        Assert.Contains("connection recovery did not complete within", ex.Message);
    }
}
