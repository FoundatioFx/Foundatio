using Foundatio.Queues;
using Microsoft.Azure.ServiceBus;

namespace Foundatio.Extensions
{
    internal static class QueueEntryExtensions {
        
        public static string LockToken(this IQueueEntry entry) => (string) entry.Properties["LockToken"];

        public static void SetLockToken(this IQueueEntryMetadata entry, Message message) => entry.Properties.Add("LockToken", message.SystemProperties.LockToken);
    }
}