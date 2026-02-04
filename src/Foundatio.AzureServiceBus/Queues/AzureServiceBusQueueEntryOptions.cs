using Foundatio.Queues;

namespace Foundatio.AzureServiceBus.Queues;

public record AzureServiceBusQueueEntryOptions : QueueEntryOptions
{
    public string SessionId { get; set; }
}
