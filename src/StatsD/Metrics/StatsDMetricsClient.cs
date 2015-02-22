using System;
using Foundatio.Metrics;
using StatsdClient;

namespace Foundatio.AppStats {
    public class StatsDMetricsClient : IMetricsClient {
        private readonly IStatsd _client;

        public StatsDMetricsClient(string serverName = "127.0.0.1", int port = 12000, string prefix = "stats") {
            _client = new Statsd(serverName, port, prefix: prefix, connectionType: ConnectionType.Udp);
        }

        public void Counter(string statName, int value = 1) {
            _client.LogCount(statName, value);
        }

        public void Gauge(string statName, double value) {
            _client.LogGauge(statName, (int)value);
        }

        public void Timer(string statName, long milliseconds) {
            _client.LogTiming(statName, milliseconds);
        }

        public IDisposable StartTimer(string statName) {
            return new MetricTimer(statName, this);
        }

        public void Time(Action action, string statName) {
            using (StartTimer(statName))
                action();
        }

        public T Time<T>(Func<T> func, string statName) {
            using (StartTimer(statName))
                return func();
        }

        public void Dispose() {}
    }
}