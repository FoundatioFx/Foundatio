using Foundatio.Metrics;

namespace Foundatio.Tests.Queue {
    public class SimpleWorkItem : IHaveSubMetricName {
        public string Data { get; set; }
        public int Id { get; set; }

        public string GetSubMetricName() {
            return Data.Trim();
        }
    }
}
