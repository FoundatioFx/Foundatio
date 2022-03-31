using System;

namespace Foundatio.Metrics {
    public class DiagnosticsMetricsClientOptions : SharedMetricsClientOptions {
        public string MeterName { get; set; }
        public string MeterVersion { get; set; }
    }

    public class DiagnosticsMetricsClientOptionsBuilder : SharedMetricsClientOptionsBuilder<DiagnosticsMetricsClientOptions, DiagnosticsMetricsClientOptionsBuilder> {
        public DiagnosticsMetricsClientOptionsBuilder MeterName(string name) {
            if (String.IsNullOrEmpty(name))
                throw new ArgumentNullException(nameof(name));
            Target.MeterName = name;
            return this;
        }

        public DiagnosticsMetricsClientOptionsBuilder MeterVersion(string version) {
            if (String.IsNullOrEmpty(version))
                throw new ArgumentNullException(nameof(version));
            Target.MeterVersion = version;
            return this;
        }
    }
}