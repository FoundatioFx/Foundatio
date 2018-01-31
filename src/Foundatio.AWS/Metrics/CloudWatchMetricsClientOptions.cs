using System;

namespace Foundatio.Metrics {
    public class CloudWatchMetricsClientOptions : SharedMetricsClientOptions {
        public string ConnectionString { get; set; }
    }

    public class CloudWatchMetricsClientOptionsBuilder : SharedMetricsClientOptionsBuilder<CloudWatchMetricsClientOptions, CloudWatchMetricsClientOptionsBuilder> {
        public CloudWatchMetricsClientOptionsBuilder ConnectionString(string connectionString) {
            if (string.IsNullOrEmpty(connectionString))
                throw new ArgumentNullException(nameof(connectionString));
            Target.ConnectionString = connectionString;
            return this;
        }
    }
}