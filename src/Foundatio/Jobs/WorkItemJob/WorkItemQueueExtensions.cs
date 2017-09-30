using System;
using System.Threading.Tasks;
using Foundatio.Utility;
using Foundatio.Queues;
using Foundatio.Serializer;

namespace Foundatio.Jobs {
    public static class WorkItemQueueExtensions {
        public static Task<string> EnqueueAsync<T>(this IQueue<WorkItemData> queue, T workItemData, bool includeProgressReporting = false) {
            string id = Guid.NewGuid().ToString("N");
            string json = queue.Serializer.SerializeToString(workItemData);
            return queue.EnqueueAsync(new WorkItemData {
                Data = json,
                WorkItemId = id,
                Type = typeof(T).AssemblyQualifiedName,
                SendProgressReports = includeProgressReporting
            });
        }
    }
}