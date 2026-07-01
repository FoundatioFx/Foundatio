using Foundatio.Messaging;

namespace Foundatio.MessagingSample;

/// <summary>A short per-process id so you can see which instance handled each message/job when scaled to replicas.</summary>
public sealed record InstanceInfo(string Id);

/// <summary>
/// Handles orders. Registration carries no topology — orders arrive here because the endpoint calls
/// <c>bus.SendAsync</c>, so exactly one running instance processes each order (competing consumers). Resolved from DI
/// per message; throwing would trigger retry/dead-letter.
/// </summary>
public sealed class ProcessOrderHandler(InstanceInfo instance, ILogger<ProcessOrderHandler> logger) : IMessageHandler<ProcessOrder>
{
    public Task HandleAsync(IReceivedMessage<ProcessOrder> message, CancellationToken cancellationToken)
    {
        logger.LogInformation("[{Instance}] processed order: {Quantity} x {Product}", instance.Id, message.Message.Quantity, message.Message.Product);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Handles announcements published via <c>bus.PublishAsync</c>. Registered with <c>PerInstance = true</c>, so every
/// running replica receives its own copy — without it, the default is once per service (replicas compete).
/// </summary>
public sealed class AnnouncementHandler(InstanceInfo instance, ILogger<AnnouncementHandler> logger) : IMessageHandler<Announcement>
{
    public Task HandleAsync(IReceivedMessage<Announcement> message, CancellationToken cancellationToken)
    {
        logger.LogInformation("[{Instance}] announcement: {Text}", instance.Id, message.Message.Text);
        return Task.CompletedTask;
    }
}
