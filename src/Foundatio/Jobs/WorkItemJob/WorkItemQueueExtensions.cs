using System;
using System.Threading.Tasks;
using Foundatio.Metrics;
using Foundatio.Queues;
using Foundatio.Serializer;
using Foundatio.Utility;

namespace Foundatio.Jobs
{
    public static class WorkItemQueueExtensions
    {
        public static async Task<string> EnqueueAsync<T>(this IQueue<WorkItemData> queue, T workItemData, bool includeProgressReporting = false)
        {
            string jobId = Guid.NewGuid().ToString("N");
            var bytes = queue.Serializer.SerializeToBytes(workItemData);

            var data = new WorkItemData
            {
                Data = bytes,
                WorkItemId = jobId,
                Type = typeof(T).AssemblyQualifiedName,
                SendProgressReports = includeProgressReporting
            };

            if (workItemData is IHaveUniqueIdentifier haveUniqueIdentifier)
                data.UniqueIdentifier = haveUniqueIdentifier.UniqueIdentifier;

            if (workItemData is IHaveSubMetricName haveSubMetricName && haveSubMetricName.SubMetricName != null)
                data.SubMetricName = haveSubMetricName.SubMetricName;
            else
                data.SubMetricName = GetDefaultSubMetricName(data);

            await queue.EnqueueAsync(data).AnyContext();

            return jobId;
        }

        private static string GetDefaultSubMetricName(WorkItemData data)
        {
            if (String.IsNullOrEmpty(data.Type))
                return null;

            string type = GetTypeName(data.Type);
            if (type != null && type.EndsWith("WorkItem"))
                type = type.Substring(0, type.Length - 8);

            return type?.ToLowerInvariant();
        }

        private static string GetTypeName(string assemblyQualifiedName)
        {
            if (String.IsNullOrEmpty(assemblyQualifiedName))
                return null;

            var parts = assemblyQualifiedName.Split(',');
            int i = parts[0].LastIndexOf('.');

            return i < 0 ? null : parts[0].Substring(i + 1);
        }
    }
}
