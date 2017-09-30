using System;
using System.Threading.Tasks;
using Foundatio.Utility;
using Foundatio.Queues;
using Foundatio.Serializer;

namespace Foundatio.Jobs {
    public static class WorkItemQueueExtensions {
        public static async Task<string> EnqueueAsync<T>(this IQueue<WorkItemData> queue, T workItemData, bool includeProgressReporting = false) {
            string jobId = Guid.NewGuid().ToString("N");
            string json = queue.Serializer.SerializeToString(workItemData);
            await queue.EnqueueAsync(new WorkItemData {
                Data = json,
                WorkItemId = jobId,
                Type = typeof(T).AssemblyQualifiedName,
                SendProgressReports = includeProgressReporting
            }).AnyContext();

            return jobId;
        }
    }
}