using Foundatio.Queues;

namespace Foundatio.AzureServiceBus.Queues {
    public class AzureServiceBusQueueEntryOptions : QueueEntryOptions {
        public string SessionId { get; set; }
    }
}