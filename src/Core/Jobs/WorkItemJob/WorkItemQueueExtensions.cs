using System;
using Foundatio.Queues;
using Foundatio.Serializer;

namespace Foundatio.Jobs {
    public static class WorkItemQueueExtensions
    {
        public static string Enqueue<T>(this IQueue<WorkItemData> queue, T workItemData, bool includeProgressReporting = false) {
            string id = Guid.NewGuid().ToString("N");
            var json = queue.Serializer.SerializeToString(workItemData);
            queue.Enqueue(new WorkItemData {
                Data = json,
                WorkItemId = id,
                Type = typeof(T).AssemblyQualifiedName,
                SendProgressReports = includeProgressReporting
            });

            return id;
        }
    }
}