using Foundatio.Metrics;
using Foundatio.Queues;

namespace Foundatio.Jobs;

public class WorkItemData : IHaveSubMetricName, IHaveUniqueIdentifier
{
    public string WorkItemId { get; set; }
    public string Type { get; set; }
    public byte[] Data { get; set; }
    public bool SendProgressReports { get; set; }
    public string UniqueIdentifier { get; set; }
    public string SubMetricName { get; set; }
}
