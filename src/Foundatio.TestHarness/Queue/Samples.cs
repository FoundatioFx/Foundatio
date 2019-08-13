using Foundatio.Metrics;
using Foundatio.Queues;

namespace Foundatio.Tests.Queue {
    public class SimpleWorkItem : IHaveSubMetricName, IHaveUniqueIdentifier {
        public string Data { get; set; }
        public int Id { get; set; }
        public string UniqueIdentifier { get; set; }
        public string SubMetricName { get; set; }
    }
}
