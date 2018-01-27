using System;

namespace Foundatio.Metrics {
    public class StatsDMetricsClientOptions : MetricsClientOptionsBase {
        public string ServerName { get; set; }
        public int Port { get; set; } = 8125;
    }

    public static class StatsDMetricsClientOptionsExtensions {
        public static StatsDMetricsClientOptions WithServer(this StatsDMetricsClientOptions options, string serverName, int port = 8125) {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            if (String.IsNullOrEmpty(serverName))
                throw new ArgumentNullException(nameof(serverName));
            options.ServerName = serverName;
            options.Port = port;
            return options;
        }
    }
}
