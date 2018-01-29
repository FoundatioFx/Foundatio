using System;

namespace Foundatio.Metrics {
    public class StatsDMetricsClientOptions : SharedMetricsClientOptions {
        public string ServerName { get; set; }
        public int Port { get; set; } = 8125;
    }

    public class StatsDMetricsClientOptionsBuilder : OptionsBuilder<StatsDMetricsClientOptions>, ISharedMetricsClientOptionsBuilder {}

    public static class StatsDMetricsClientOptionsExtensions {
        public static T Server<T>(this T builder, string serverName, int port = 8125) where T: ISharedMetricsClientOptionsBuilder {
            if (builder == null)
                throw new ArgumentNullException(nameof(builder));
            if (String.IsNullOrEmpty(serverName))
                throw new ArgumentNullException(nameof(serverName));
            builder.Target<StatsDMetricsClientOptions>().ServerName = serverName;
            builder.Target<StatsDMetricsClientOptions>().Port = port;
            return builder;
        }
    }
}
