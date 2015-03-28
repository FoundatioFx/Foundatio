using System;
using System.Threading.Tasks;
using Foundatio.Metrics;
using StatsdClient;

namespace Foundatio.AppStats {
    public class StatsDMetricsClient : IMetricsClient {
        private readonly IStatsd _client;

        public StatsDMetricsClient(string serverName = "127.0.0.1", int port = 12000, string prefix = "stats") {
            _client = new Statsd(serverName, port, prefix: prefix, connectionType: ConnectionType.Udp);
        }

        public Task CounterAsync(string statName, int value = 1) {
            return Task.Run(() => _client.LogCount(statName, value));
        }

        public Task GaugeAsync(string statName, double value) {
            return Task.Run(() => _client.LogGauge(statName, (int)value));
        }

        public Task TimerAsync(string statName, long milliseconds) {
            return Task.Run(() => _client.LogTiming(statName, milliseconds));
        }

        public void Dispose() {}
    }
}