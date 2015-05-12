using System;
using System.Threading.Tasks;

using Foundatio.Utility;
using Metrics;

namespace Foundatio.Metrics {
    public class MetricsNETClient : IMetricsClient {
        public Task CounterAsync(string statName, int value = 1) {
            Metric.Counter(statName, Unit.None).Increment();
            return TaskHelper.Completed();
        }

        public Task GaugeAsync(string statName, double value) {
            Metric.Gauge(statName, () => value, Unit.None);
            return TaskHelper.Completed();
        }

        public Task TimerAsync(string statName, long milliseconds) {
            Metric.Timer(statName, Unit.Calls, SamplingType.SlidingWindow, TimeUnit.Milliseconds).Record(milliseconds, TimeUnit.Milliseconds);
            return TaskHelper.Completed();
        }

        public void Dispose() {}
    }
}