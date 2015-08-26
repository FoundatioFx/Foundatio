using System.Reflection;
using Foundatio.Metrics;

namespace Foundatio.Jobs {
    public class WorkItemData : IMetricStatName {
        public string WorkItemId { get; set; }
        public string Type { get; set; }
        public string Data { get; set; }
        public bool SendProgressReports { get; set; }


        public string GetStatName()
        {
            if (string.IsNullOrEmpty(Type))
                return null;

            var type = System.Type.GetType(Type, false);

            return type?.Name.ToLowerInvariant();
        }
    }
}