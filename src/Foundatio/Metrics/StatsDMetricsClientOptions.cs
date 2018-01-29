using System;

namespace Foundatio.Metrics {
    public class StatsDMetricsClientOptions : SharedMetricsClientOptions {
        public string ServerName { get; set; }
        public int Port { get; set; } = 8125;
    }

    public class StatsDMetricsClientOptionsBuilder : OptionsBuilder<StatsDMetricsClientOptions>, ISharedMetricsClientOptionsBuilder {
        public StatsDMetricsClientOptionsBuilder Server(string serverName, int port = 8125) {
            if (String.IsNullOrEmpty(serverName))
                throw new ArgumentNullException(nameof(serverName));
            Target.ServerName = serverName;
            Target.Port = port;
            return this;
        }
    }
}
