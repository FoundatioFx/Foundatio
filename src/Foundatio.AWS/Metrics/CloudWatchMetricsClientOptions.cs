using System;

namespace Foundatio.Metrics {
    public class CloudWatchMetricsClientOptions : MetricsClientOptionsBase {
        public string ConnectionString { get; set; }
    }

    public static class CloudWatchMetricsClientOptionsExtensions {
        public static IOptionsBuilder<CloudWatchMetricsClientOptions> ConnectionString(this IOptionsBuilder<CloudWatchMetricsClientOptions> builder, string connectionString) {
            if (builder == null)
                throw new ArgumentNullException(nameof(builder));
            if (string.IsNullOrEmpty(connectionString))
                throw new ArgumentNullException(nameof(connectionString));
            builder.Target.ConnectionString = connectionString;
            return builder;
        }
    }
}