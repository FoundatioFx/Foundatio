using System;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Nito.AsyncEx;
using NLog.Fluent;

namespace Foundatio.Metrics {
    public class StatsDMetricsClient : IMetricsClient {
        private readonly AsyncLock _lock = new AsyncLock();
        private UdpClient _udpClient;
        private readonly string _prefix;
        private readonly string _serverName;
        private readonly int _port;

        public StatsDMetricsClient(string serverName = "127.0.0.1", int port = 12000, string prefix = "stats") {
            _serverName = serverName;
            _port = port;

            if (!String.IsNullOrEmpty(prefix))
                _prefix = prefix.EndsWith(".") ? prefix : String.Concat(prefix, ".");
        }

        public Task CounterAsync(string statName, int value = 1) {
            return TrySendAsync(BuildMetric("c", statName, value.ToString(CultureInfo.InvariantCulture)));
        }

        public Task GaugeAsync(string statName, double value) {
            return TrySendAsync(BuildMetric("g", statName, value.ToString(CultureInfo.InvariantCulture)));
        }

        public Task TimerAsync(string statName, long milliseconds) {
            return TrySendAsync(BuildMetric("ms", statName, milliseconds.ToString(CultureInfo.InvariantCulture)));
        }

        private string BuildMetric(string type, string statName, string value) {
            return String.Format("{0}{1}:{2}|{3}", _prefix, statName, value, type);
        }

        private async Task TrySendAsync(string metric) {
            if (String.IsNullOrEmpty(metric))
                return;

            try {
                using (await _lock.LockAsync()) {
                    if (_udpClient == null)
                        _udpClient = new UdpClient(_serverName, _port);

                    byte[] data = Encoding.ASCII.GetBytes(metric);
                    await _udpClient.SendAsync(data, data.Length);
                }
            } catch (Exception ex) {
                Log.Error().Exception(ex).Message("An error occurred while sending the metrics.").Write();
                var se = ex as SocketException;
                if (se != null && se.ErrorCode == 10022) {
                    Log.Info().Message("Attempting to reset the timed out udp client.").Write();
                    ResetUdpClient();
                }
            }
        }

        private void ResetUdpClient() {
            if (_udpClient == null)
                return;

            using (_lock.Lock()) {
                try {
                    _udpClient.Close();
                } catch (Exception ex) {
                    Log.Error().Exception(ex).Message("An error occurred while calling Close() on the udp client.").Write();
                } finally {
                    _udpClient = null;
                }
            }
        }

        public void Dispose() {
            ResetUdpClient();
        }
    }
}