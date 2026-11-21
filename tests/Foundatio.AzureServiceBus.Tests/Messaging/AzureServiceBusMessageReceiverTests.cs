using System;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Foundatio.Messaging.Transport;
using Foundatio.Xunit;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Foundatio.AzureServiceBus.Tests.Messaging;

/// <summary>
/// Tests the receiver without sessions against <see cref="FakeServiceBusQueueClient"/>, for what depends on when a
/// message arrives during a receive.
/// </summary>
public class AzureServiceBusMessageReceiverTests(ITestOutputHelper output) : TestWithLoggingBase(output)
{
    [Fact]
    public async Task ReceiveAsync_WhenTheMessageArrivesAfterAPollLongerThanTheLock_ReportsTheLockFromWhenItArrived()
    {
        // Arrange
        var client = new FakeServiceBusQueueClient();
        var timeProvider = new FakeTimeProvider();
        await using var transport = CreateTransport(client, timeProvider);
        await using var receiver = await transport.CreateReceiverAsync(MessageSource.Queue("orders"), new ReceiverOptions(), TestCancellationToken);

        // Act
        var message = await ReceiveAfterAPollLongerThanTheLockAsync(client, timeProvider, receiver);

        // Assert
        var receivedAt = timeProvider.GetUtcNow();
        Assert.NotNull(message.LeaseExpiresAt);
        Assert.InRange(message.LeaseExpiresAt.Value, receivedAt + client.LockDuration / 2, receivedAt + client.LockDuration);
    }

    [Fact]
    public async Task AbandonAsync_RightAfterAPollLongerThanTheLock_AbandonsTheMessage()
    {
        // Arrange
        var client = new FakeServiceBusQueueClient();
        var timeProvider = new FakeTimeProvider();
        await using var transport = CreateTransport(client, timeProvider);
        await using var receiver = await transport.CreateReceiverAsync(MessageSource.Queue("orders"), new ReceiverOptions(), TestCancellationToken);
        var message = await ReceiveAfterAPollLongerThanTheLockAsync(client, timeProvider, receiver);

        // Act
        await receiver.AbandonAsync(message, TimeSpan.Zero, TestCancellationToken);

        // Assert
        Assert.Equal(1, client.Abandons);
    }

    [Fact]
    public async Task DisposeAsync_RightAfterAPollLongerThanTheLock_ReleasesTheMessage()
    {
        // Arrange
        var client = new FakeServiceBusQueueClient();
        var timeProvider = new FakeTimeProvider();
        await using var transport = CreateTransport(client, timeProvider);
        var receiver = await transport.CreateReceiverAsync(MessageSource.Queue("orders"), new ReceiverOptions(), TestCancellationToken);
        await ReceiveAfterAPollLongerThanTheLockAsync(client, timeProvider, receiver);

        // Act
        await receiver.DisposeAsync();

        // Assert
        Assert.Equal(1, client.Abandons);
    }

    private AzureServiceBusMessageTransport CreateTransport(FakeServiceBusQueueClient client, TimeProvider timeProvider) =>
        new(client, new LockDurationAdministrationClient(client.LockDuration), new AzureServiceBusMessageTransportOptions
        {
            LockDuration = client.LockDuration,
            TimeProvider = timeProvider,
            LoggerFactory = Log
        });

    /// <summary>Starts a receive on the empty queue, and sends one message once the poll has waited longer than the lock lasts.</summary>
    private async Task<IncomingMessage> ReceiveAfterAPollLongerThanTheLockAsync(FakeServiceBusQueueClient client, FakeTimeProvider timeProvider, IMessageReceiver receiver)
    {
        var receive = receiver.ReceiveAsync(1, TimeSpan.FromSeconds(30), TestCancellationToken);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (client.WaitingReceives == 0)
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("The receive did not start polling in time.");

            await Task.Delay(5, TestCancellationToken);
        }

        timeProvider.Advance(client.LockDuration + TimeSpan.FromSeconds(1));
        client.Send("order-1");
        return Assert.Single(await receive);
    }

    /// <summary>Reports every queue as existing, without sessions, with one lock duration.</summary>
    private sealed class LockDurationAdministrationClient(TimeSpan lockDuration) : ServiceBusAdministrationClient
    {
        public override Task<Response<QueueProperties>> GetQueueAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult(Response.FromValue(ServiceBusModelFactory.QueueProperties(name, lockDuration, 1024, false, false, TimeSpan.MaxValue, TimeSpan.MaxValue,
                false, TimeSpan.FromMinutes(10), 10, true, EntityStatus.Active, null, null, String.Empty, false), null!));
    }
}
