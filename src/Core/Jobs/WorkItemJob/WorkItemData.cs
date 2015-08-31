using Foundatio.Metrics;

namespace Foundatio.Jobs {
    public class WorkItemData : IHaveMetricName {
        public string WorkItemId { get; set; }
        public string Type { get; set; }
        public string Data { get; set; }
        public bool SendProgressReports { get; set; }

        public string GetMetricName()
        {
            if (string.IsNullOrEmpty(Type))
                return null;

            var type = System.Type.GetType(Type, false);

            return type?.Name.ToLowerInvariant();
        }
    }
}