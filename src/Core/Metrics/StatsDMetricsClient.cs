using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Foundatio.Logging;
using Foundatio.Utility;

namespace Foundatio.Metrics {
    public class StatsDMetricsClient : IMetricsClient {
        private readonly object _lock = new object();
        private Socket _socket;
        private readonly IPEndPoint _endPoint;
        private readonly string _prefix;

        public StatsDMetricsClient(string serverName = "127.0.0.1", int port = 8125, string prefix = null) {
            _endPoint = new IPEndPoint(IPAddress.Parse(serverName), port);

            if (!String.IsNullOrEmpty(prefix))
                _prefix = prefix.EndsWith(".") ? prefix : String.Concat(prefix, ".");
        }

        public Task CounterAsync(string statName, int value = 1) {
            Send(BuildMetric("c", statName, value.ToString(CultureInfo.InvariantCulture)));
            return TaskHelper.Completed();
        }

        public Task GaugeAsync(string statName, double value) {
            Send(BuildMetric("g", statName, value.ToString(CultureInfo.InvariantCulture)));
            return TaskHelper.Completed();
        }

        public Task TimerAsync(string statName, int milliseconds) {
            Send(BuildMetric("ms", statName, milliseconds.ToString(CultureInfo.InvariantCulture)));
            return TaskHelper.Completed();
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
                _socket?.SendTo(data, _endPoint);
            } catch (Exception ex) {
                Logger.Error().Exception(ex).Message("An error occurred while sending the metrics: {0}", ex.Message).Write();
                ResetUdpClient();
            }
        }

        private void EnsureSocket() {
            if (_socket != null)
                return;

            lock (_lock) {
                if (_socket == null)
                    _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            }
        }

        private void ResetUdpClient() {
            if (_socket == null)
                return;

            lock (_lock) {
                if (_socket == null)
                    return;

                try {
                    _socket.Close();
                } catch (Exception ex) {
                    Logger.Error().Exception(ex).Message("An error occurred while calling Close() on the socket.").Write();
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