using System.Threading.Tasks;
using Metrics;

namespace Foundatio.Metrics {
    public class MetricsNETClient : IMetricsClient {
        public void Counter(string name, int value = 1) {
            Metric.Counter(name, Unit.None).Increment();
        }

        public void Gauge(string name, double value) {
            Metric.Gauge(name, () => value, Unit.None);
        }

        public void Timer(string name, int milliseconds) {
            Metric.Timer(name, Unit.Calls, SamplingType.SlidingWindow, TimeUnit.Milliseconds).Record(milliseconds, TimeUnit.Milliseconds);
        }

        public void Dispose() {}
    }
}