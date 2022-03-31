using System;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace Foundatio.Metrics {
    public class DiagnosticsMetricsClient : IMetricsClient {
        private readonly ConcurrentDictionary<string, Counter<int>> _counters = new();
        private readonly ConcurrentDictionary<string, GaugeInfo> _gauges = new();
        private readonly ConcurrentDictionary<string, Histogram<int>> _timers = new();
        private readonly Meter _meter;
        private readonly string _prefix;

        public DiagnosticsMetricsClient() : this(o => o) { }

        public DiagnosticsMetricsClient(DiagnosticsMetricsClientOptions options) {
            _prefix = !String.IsNullOrEmpty(options.Prefix) ? (!options.Prefix.EndsWith(":") ? options.Prefix + ":" : options.Prefix) : String.Empty;
            _meter = new Meter(options.MeterName ?? "Foundatio.MetricsClient", options.MeterVersion ?? FoundatioDiagnostics.AssemblyName.Version.ToString());
        }

        public DiagnosticsMetricsClient(Builder<DiagnosticsMetricsClientOptionsBuilder, DiagnosticsMetricsClientOptions> config)
            : this(config(new DiagnosticsMetricsClientOptionsBuilder()).Build()) { }

        public void Counter(string name, int value = 1) {
            var counter = _counters.GetOrAdd(_prefix + name, _meter.CreateCounter<int>(name));
            counter.Add(value);
        }

        public void Gauge(string name, double value) {
            var gauge = _gauges.GetOrAdd(_prefix + name, new GaugeInfo(_meter, name));
            gauge.Value = value;
        }

        public void Timer(string name, int milliseconds) {
            var timer = _timers.GetOrAdd(_prefix + name, _meter.CreateHistogram<int>(name, "ms"));
            timer.Record(milliseconds);
        }

        public void Dispose() {
            _meter.Dispose();
        }

        private class GaugeInfo {
            public GaugeInfo(Meter meter, string name) {
                Gauge = meter.CreateObservableGauge(name, () => Value);
            }

            public ObservableGauge<double> Gauge { get; }
            public double Value { get; set; }
        }
    }
}
