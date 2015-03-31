using System;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Foundatio.Metrics {
    public class StatsDMetricsClient : IMetricsClient {
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
                var client = GetClient();
                if (client == null)
                    return;

                byte[] data = Encoding.ASCII.GetBytes(metric);
                await client.SendAsync(data, data.Length);
            } catch (SocketException ex) {
                Trace.TraceError("An error occurred while sending the metrics. Message: {0}", ex.Message);
                if (ex.ErrorCode == 10022) {
                    Trace.TraceInformation("Attempting to reset the timed out client.");
                    ResetUdpClient();
                }
            } catch (Exception ex) {
                Trace.TraceError("An error occurred while sending the metrics. Message: {0}", ex.Message);
            }
        }

        private UdpClient _udpClient;
        private UdpClient GetClient() {
            if (_udpClient == null)
                _udpClient = new UdpClient(_serverName, _port);

            return _udpClient;
        }

        private void ResetUdpClient() {
            if (_udpClient == null)
                return;

            try {
                _udpClient.Close();
            } catch (Exception ex) {
                Trace.TraceError("An error occurred while calling Close() on the udp client. Message: {0}", ex.Message);
            } finally {
                _udpClient = null;
            }
        }

        public void Dispose() {
            ResetUdpClient();
        }
    }
}