using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Nito.AsyncEx;
using NLog.Fluent;

namespace Foundatio.Metrics {
    public class StatsDMetricsClient : IMetricsClient {
        private readonly AsyncLock _lock = new AsyncLock();
        private Socket _socket;
        private readonly IPEndPoint _endPoint;
        private readonly string _prefix;

        public StatsDMetricsClient(string serverName = "127.0.0.1", int port = 12000, string prefix = "stats") {
            _endPoint = new IPEndPoint(IPAddress.Parse(serverName), port);

            if (!String.IsNullOrEmpty(prefix))
                _prefix = prefix.EndsWith(".") ? prefix : String.Concat(prefix, ".");
        }

        public Task CounterAsync(string statName, int value = 1) {
            Send(BuildMetric("c", statName, value.ToString(CultureInfo.InvariantCulture)));
            return Task.FromResult(0);
        }

        public Task GaugeAsync(string statName, double value) {
            Send(BuildMetric("g", statName, value.ToString(CultureInfo.InvariantCulture)));
            return Task.FromResult(0);
        }

        public Task TimerAsync(string statName, long milliseconds) {
            Send(BuildMetric("ms", statName, milliseconds.ToString(CultureInfo.InvariantCulture)));
            return Task.FromResult(0);
        }

        private string BuildMetric(string type, string statName, string value) {
            return String.Concat(_prefix, statName, ":", value, "|", type);
        }

        private void Send(string metric) {
            if (String.IsNullOrEmpty(metric))
                return;

            try {
                byte[] data = Encoding.ASCII.GetBytes(metric);

                EnsureSocket();
                if (_socket != null)
                    _socket.SendTo(data, _endPoint);
            } catch (Exception ex) {
                Log.Error().Exception(ex).Message("An error occurred while sending the metrics.").Write();
                var se = ex as SocketException;
                if (se != null && se.ErrorCode == 10022) {
                    Log.Info().Message("Attempting to reset the timed out socket.").Write();
                    ResetUdpClient();
                }
            }
        }

        private void EnsureSocket() {
            if (_socket != null)
                return;

            using (_lock.Lock()) {
                if (_socket == null)
                    _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            }
        }

        private void ResetUdpClient() {
            if (_socket == null)
                return;

            using (_lock.Lock()) {
                try {
                    _socket.Close();
                } catch (Exception ex) {
                    Log.Error().Exception(ex).Message("An error occurred while calling Close() on the socket.").Write();
                } finally {
                    _socket = null;
                }
            }
        }

        public void Dispose() {
            ResetUdpClient();
        }
    }
}