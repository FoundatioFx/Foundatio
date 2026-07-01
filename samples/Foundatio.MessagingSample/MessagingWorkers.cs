using Foundatio.Messaging;

namespace Foundatio.MessagingSample;

/// <summary>A short per-process id so you can see which instance handled each message/job when scaled to replicas.</summary>
public sealed record InstanceInfo(string Id);

/// <summary>
/// Starts this instance's long-running queue consumer and pub/sub subscriber for the app's lifetime — the idiomatic
/// way to host Foundatio consumers in ASP.NET. Handlers auto-complete on success (<see cref="AckMode.Auto"/>); throwing
/// triggers the core's retry/dead-letter policy.
/// </summary>
public sealed class MessagingWorkers(IQueue queue, IPubSub pubSub, InstanceInfo instance, ILogger<MessagingWorkers> logger) : IHostedService
{
    private IMessageConsumer? _orderConsumer;
    private IMessageSubscription? _announcementSubscription;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Competing consumers: the shared "orders" queue load-balances across every running instance, so each order is
        // processed exactly once. Scale the service up and the work spreads out.
        _orderConsumer = await queue.StartConsumerAsync<ProcessOrder>((message, _) =>
        {
            logger.LogInformation("[{Instance}] processed order: {Quantity} x {Product}", instance.Id, message.Message.Quantity, message.Message.Product);
            return Task.CompletedTask;
        }, cancellationToken: cancellationToken);

        // Fan-out: a per-instance subscription means every instance receives every announcement (broadcast). Using a
        // shared subscription name here would instead load-balance the topic like the queue above.
        _announcementSubscription = await pubSub.SubscribeAsync<Announcement>((message, _) =>
        {
            logger.LogInformation("[{Instance}] announcement: {Text}", instance.Id, message.Message.Text);
            return Task.CompletedTask;
        }, new PubSubSubscriptionOptions { Subscription = instance.Id }, cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_orderConsumer is not null)
            await _orderConsumer.DisposeAsync();
        if (_announcementSubscription is not null)
            await _announcementSubscription.DisposeAsync();
    }
}
