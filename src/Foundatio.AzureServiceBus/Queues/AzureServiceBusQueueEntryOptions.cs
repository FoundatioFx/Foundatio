using Foundatio.Queues;

namespace Foundatio.AzureServiceBus.Queues;

public record AzureServiceBusQueueEntryOptions : QueueEntryOptions
{
    /// <summary>
    /// The Service Bus session id. Overrides <see cref="QueueEntryOptions.GroupId"/> when both are set.
    /// </summary>
    public string? SessionId { get; set; }
}
