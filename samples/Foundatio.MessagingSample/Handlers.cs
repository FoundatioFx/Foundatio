using Foundatio.Messaging;

namespace Foundatio.MessagingSample;

/// <summary>A short per-process id so you can see which instance handled each message/job when scaled to replicas.</summary>
public sealed record InstanceInfo(string Id);

/// <summary>
/// Handles orders. Registration carries no topology — orders arrive here because the endpoint calls
/// <c>bus.SendAsync</c>, and running instances compete for deliveries. Resolved from DI
/// per message; throwing would trigger retry/dead-letter.
/// </summary>
public sealed class ProcessOrderHandler(InstanceInfo instance, ILogger<ProcessOrderHandler> logger) : IMessageHandler<ProcessOrder>
{
    public Task HandleAsync(IMessageContext<ProcessOrder> context, CancellationToken cancellationToken)
    {
        logger.LogInformation("[{Instance}] processed order: {Quantity} x {Product}", instance.Id, context.Message.Quantity, context.Message.Product);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Handles announcements published via <c>bus.PublishAsync</c>. The service name supplies the durable subscription
/// identity; this service's replicas compete for each delivery.
/// </summary>
public sealed class AnnouncementHandler(InstanceInfo instance, ILogger<AnnouncementHandler> logger) : IMessageHandler<Announcement>
{
    public Task HandleAsync(IMessageContext<Announcement> context, CancellationToken cancellationToken)
    {
        logger.LogInformation("[{Instance}] announcement: {Text}", instance.Id, context.Message.Text);
        return Task.CompletedTask;
    }
}
