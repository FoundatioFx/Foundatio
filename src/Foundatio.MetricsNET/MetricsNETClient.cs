using System;
using System.Threading.Tasks;
using Metrics;

namespace Foundatio.Metrics {
    public class MetricsNETClient : IMetricsClient {
        public Task CounterAsync(string name, int value = 1) {
            Metric.Counter(name, Unit.None).Increment();
            return Task.CompletedTask;
        }

        public Task GaugeAsync(string name, double value) {
            Metric.Gauge(name, () => value, Unit.None);
            return Task.CompletedTask;
        }

        public Task TimerAsync(string name, int milliseconds) {
            Metric.Timer(name, Unit.Calls, SamplingType.SlidingWindow, TimeUnit.Milliseconds).Record(milliseconds, TimeUnit.Milliseconds);
            return Task.CompletedTask;
        }

        public void Dispose() {}
    }
}