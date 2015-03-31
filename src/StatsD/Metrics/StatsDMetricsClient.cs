using System;
using System.Threading.Tasks;
using Foundatio.StatsD;

namespace Foundatio.Metrics {
    public class StatsDMetricsClient : IMetricsClient {
        private IStatsDClient _client;

        public StatsDMetricsClient(IStatsDClient client) {
            _client = client;
        }

        public StatsDMetricsClient(string serverName = "127.0.0.1", int port = 12000, string prefix = "stats") {
            _client = new UdpStatsDClient(serverName, port, prefix);
        }

        public async Task CounterAsync(string statName, int value = 1) {
            if (_client == null)
                return;

            await _client.CounterAsync(statName, value);
        }

        public async Task GaugeAsync(string statName, double value) {
            if (_client == null)
                return;

            await _client.GaugeAsync(statName, value);
        }

        public async Task TimerAsync(string statName, long milliseconds) {
            if (_client == null)
                return;

            await _client.TimerAsync(statName, milliseconds);
        }

        public void Dispose() {
            if (_client == null)
                return;

            _client.Dispose();
            _client = null;
        }
    }
}