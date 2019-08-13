using System;
using System.Threading.Tasks;
using Foundatio.Metrics;
using Foundatio.Utility;
using Foundatio.Queues;
using Foundatio.Serializer;

namespace Foundatio.Jobs {
    public static class WorkItemQueueExtensions {
        public static async Task<string> EnqueueAsync<T>(this IQueue<WorkItemData> queue, T workItemData, bool includeProgressReporting = false) {
            string jobId = Guid.NewGuid().ToString("N");
            var bytes = queue.Serializer.SerializeToBytes(workItemData);

            var data = new WorkItemData {
                Data = bytes,
                WorkItemId = jobId,
                Type = typeof(T).AssemblyQualifiedName,
                SendProgressReports = includeProgressReporting
            };

            if (workItemData is IHaveUniqueIdentifier haveUniqueIdentifier)
                data.UniqueIdentifier = haveUniqueIdentifier.UniqueIdentifier;

            if (workItemData is IHaveSubMetricName haveSubMetricName)
                data.SubMetricName = haveSubMetricName.GetSubMetricName();
            
            await queue.EnqueueAsync(data).AnyContext();

            return jobId;
        }
    }
}