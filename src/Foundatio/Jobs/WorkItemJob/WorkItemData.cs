using System;
using Foundatio.Metrics;
using Foundatio.Queues;

namespace Foundatio.Jobs {
    public class WorkItemData : IHaveSubMetricName, IHaveUniqueIdentifier {
        public string WorkItemId { get; set; }
        public string Type { get; set; }
        public byte[] Data { get; set; }
        public bool SendProgressReports { get; set; }
        public string UniqueIdentifier { get; set; }
        public string SubMetricName { get; set; }

        public string GetSubMetricName() {
            if (!String.IsNullOrEmpty(SubMetricName))
                return SubMetricName;
            
            if (String.IsNullOrEmpty(Type))
                return null;

            var type = GetTypeName(Type);
            if (type != null && type.EndsWith("WorkItem"))
                type = type.Substring(0, type.Length - 8);

            return type?.ToLowerInvariant();
        }

        public string GetTypeName(string assemblyQualifiedName) {
            if (String.IsNullOrEmpty(assemblyQualifiedName))
                return null;

            string[] parts = assemblyQualifiedName.Split(',');
            int i = parts[0].LastIndexOf('.');
            if (i < 0)
                return null;

            return parts[0].Substring(i + 1);
        }
    }
}