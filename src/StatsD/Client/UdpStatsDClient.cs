using System;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Foundatio.StatsD {
    public class UdpStatsDClient : StatsDClientBase {
        private UdpClient _udpClient;

        public UdpStatsDClient(string serverName = "127.0.0.1", int port = 12000, string prefix = null) : base(prefix) {
            _udpClient = new UdpClient(serverName, port);
        }

        protected override async Task TrySendAsync(string metric) {
            if (_udpClient == null || metric == null)
                return;

            try {
                byte[] data = Encoding.ASCII.GetBytes(metric);
                await _udpClient.SendAsync(data, data.Length);
            } catch (Exception ex) {
                Trace.TraceError("An error occurred while sending the metrics. Message: {0}", ex.Message);
            }
        }

        public override void Dispose() {
            if (_udpClient == null)
                return;

            _udpClient.Close();
            _udpClient = null;
        }
    }
}