using System;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace Foundatio.Metrics {
    public class DiagnosticsMetricsClient : IMetricsClient {
        private readonly ConcurrentDictionary<string, Counter<int>> _counters = new();
        private readonly ConcurrentDictionary<string, GaugeInfo> _gauges = new();
        private readonly ConcurrentDictionary<string, Histogram<int>> _timers = new();
        private readonly Meter _meter;

        public DiagnosticsMetricsClient(string name = null, string version = null) {
            _meter = new Meter(name ?? "Foundatio.MetricsClient", version ?? FoundatioDiagnostics.AssemblyName.Version.ToString());
        }

        public void Counter(string name, int value = 1) {
            var counter = _counters.GetOrAdd(name, _meter.CreateCounter<int>(name));
            counter.Add(value);
        }

        public void Gauge(string name, double value) {
            var gauge = _gauges.GetOrAdd(name, new GaugeInfo(_meter, name));
            gauge.Value = value;
        }

        public void Timer(string name, int milliseconds) {
            var timer = _timers.GetOrAdd(name, _meter.CreateHistogram<int>(name, "ms"));
            timer.Record(milliseconds);
        }

        public void Dispose() {}

        private class GaugeInfo {
            private readonly ObservableGauge<double> _gauge;

            public GaugeInfo(Meter meter, string name) {
                _gauge = meter.CreateObservableGauge(name, () => Value);
            }

            public ObservableGauge<double> Gauge => _gauge;
            public double Value { get; set; }
        }
    }
}
