using System;
using System.Threading.Tasks;

using Foundatio.Utility;
using Metrics;

namespace Foundatio.Metrics {
    public class MetricsNETClient : IMetricsClient {
        public Task CounterAsync(string name, int value = 1) {
            Metric.Counter(name, Unit.None).Increment();
            return TaskHelper.Completed;
        }

        public Task GaugeAsync(string name, double value) {
            Metric.Gauge(name, () => value, Unit.None);
            return TaskHelper.Completed;
        }

        public Task TimerAsync(string name, int milliseconds) {
            Metric.Timer(name, Unit.Calls, SamplingType.SlidingWindow, TimeUnit.Milliseconds).Record(milliseconds, TimeUnit.Milliseconds);
            return TaskHelper.Completed;
        }

        public void Dispose() {}
    }
}