using System;

namespace Foundatio.Metrics {
    public class CloudWatchMetricsClientOptions : MetricsClientOptionsBase {
        public string ConnectionString { get; set; }
    }

    public static class CloudWatchMetricsClientOptionsExtensions {
        public static CloudWatchMetricsClientOptions WithConnectionString(this CloudWatchMetricsClientOptions options, string connectionString) {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrEmpty(connectionString))
                throw new ArgumentNullException(nameof(connectionString));
            options.ConnectionString = connectionString;
            return options;
        }
    }
}