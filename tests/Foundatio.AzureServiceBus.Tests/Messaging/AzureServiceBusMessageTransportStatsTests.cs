using System;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Foundatio.Messaging.Transport;
using Xunit;

namespace Foundatio.AzureServiceBus.Tests.Messaging;

/// <summary>
/// The emulator reports no runtime counts, so <see cref="AzureServiceBusMessageTransport.GetStatsAsync"/> is checked
/// against an administration client that returns the counts Service Bus would.
/// </summary>
public class AzureServiceBusMessageTransportStatsTests
{
    private const string ConnectionString = "Endpoint=sb://example.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=c2VjcmV0LWtleQ==";

    [Fact]
    public async Task GetStatsAsync_Queue_CountsActiveAsQueuedScheduledAsDelayedAndNothingInFlight()
    {
        // Arrange
        var admin = new CountingAdministrationClient { Queue = ServiceBusModelFactory.QueueRuntimeProperties("dev-queue.orders", activeMessageCount: 7, scheduledMessageCount: 3, deadLetterMessageCount: 2, transferDeadLetterMessageCount: 5) };
        await using var transport = CreateTransport(admin);
        var source = MessageSource.Queue("orders");

        // Act
        var stats = await transport.GetStatsAsync(source, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(new MessageSourceStats { Source = source, Queued = 7, Delayed = 3, InFlight = null, DeadLettered = 2 }, stats);
        Assert.Equal("dev-queue.orders", admin.LastName);
    }

    [Fact]
    public async Task GetStatsAsync_Subscription_CountsNoDelayedMessages()
    {
        // Arrange
        var admin = new CountingAdministrationClient { Subscription = ServiceBusModelFactory.SubscriptionRuntimeProperties("dev-topic.events", "billing", activeMessageCount: 4, deadLetterMessageCount: 1) };
        await using var transport = CreateTransport(admin);
        var source = MessageSource.Subscription("events", "billing");

        // Act
        var stats = await transport.GetStatsAsync(source, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(new MessageSourceStats { Source = source, Queued = 4, Delayed = null, InFlight = null, DeadLettered = 1 }, stats);
        Assert.Equal("dev-topic.events/billing", admin.LastName);
    }

    [Fact]
    public async Task GetStatsAsync_SourceThatDoesNotExist_CountsAsEmpty()
    {
        await using var transport = CreateTransport(new CountingAdministrationClient());

        var queue = MessageSource.Queue("missing");
        var subscription = MessageSource.Subscription("missing", "s");

        Assert.Equal(new MessageSourceStats { Source = queue, Queued = 0, Delayed = 0, DeadLettered = 0 }, await transport.GetStatsAsync(queue, TestContext.Current.CancellationToken));
        Assert.Equal(new MessageSourceStats { Source = subscription, Queued = 0, DeadLettered = 0 }, await transport.GetStatsAsync(subscription, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetStatsAsync_InstanceSubscriptionOrNoAdministrationClient_ReturnsNull()
    {
        await using var transport = CreateTransport(new CountingAdministrationClient());
        await using var withoutAdmin = new AzureServiceBusMessageTransport(new ServiceBusClient(ConnectionString), null, new AzureServiceBusMessageTransportOptions { CreateEntities = false });

        Assert.Null(await transport.GetStatsAsync(MessageSource.InstanceSubscription("events"), TestContext.Current.CancellationToken));
        Assert.Null(await withoutAdmin.GetStatsAsync(MessageSource.Queue("orders"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetStatsAsync_DeadLetterSource_ThrowsArgumentException()
    {
        await using var transport = CreateTransport(new CountingAdministrationClient());

        await Assert.ThrowsAsync<ArgumentException>(() => transport.GetStatsAsync(MessageSource.DeadLetter(MessageSource.Queue("orders")), TestContext.Current.CancellationToken));
    }

    private static AzureServiceBusMessageTransport CreateTransport(ServiceBusAdministrationClient admin) =>
        new(new ServiceBusClient(ConnectionString), admin, new AzureServiceBusMessageTransportOptions { Prefix = "dev-" });

    private sealed class CountingAdministrationClient : ServiceBusAdministrationClient
    {
        public QueueRuntimeProperties? Queue { get; init; }
        public SubscriptionRuntimeProperties? Subscription { get; init; }
        public string? LastName { get; private set; }

        public override Task<Response<QueueRuntimeProperties>> GetQueueRuntimePropertiesAsync(string name, CancellationToken cancellationToken = default)
        {
            LastName = name;
            return Queue is null ? throw NotFound() : Task.FromResult(Response.FromValue(Queue, null!));
        }

        public override Task<Response<SubscriptionRuntimeProperties>> GetSubscriptionRuntimePropertiesAsync(string topicName, string subscriptionName, CancellationToken cancellationToken = default)
        {
            LastName = $"{topicName}/{subscriptionName}";
            return Subscription is null ? throw NotFound() : Task.FromResult(Response.FromValue(Subscription, null!));
        }

        private static ServiceBusException NotFound() => new("Not found", ServiceBusFailureReason.MessagingEntityNotFound);
    }
}
