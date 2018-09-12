using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Tests.Utility {
    public class TestUdpListener : IDisposable {
        private readonly List<string> _messages = new List<string>();
        private readonly UdpClient _listener;
        private readonly IPAddress _multicastAddress;
        private Task _receiveTask;
        private CancellationTokenSource _cancellationTokenSource;

        public TestUdpListener(string server, int port) {
            _listener = new UdpClient();
            _listener.ExclusiveAddressUse = false;
            _listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Client.ReceiveTimeout = 2000;
            _listener.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            _multicastAddress = IPAddress.Parse(server);
        }

        public string[] GetMessages() {
            return _messages.ToArray();
        }

        public void ResetMessages() {
            _messages.Clear();
        }

        public void StartListening() {
            _listener.JoinMulticastGroup(_multicastAddress);
            _cancellationTokenSource = new CancellationTokenSource();
            _receiveTask = Task.Run(() => ProcessMessages(_cancellationTokenSource.Token), _cancellationTokenSource.Token);
        }

        private async Task ProcessMessages(CancellationToken cancellationToken) {
            while (true) {
                if (cancellationToken.IsCancellationRequested)
                    return;

                var result = await _listener.ReceiveAsync();
                if (result == null || result.Buffer == null || result.Buffer.Length == 0)
                    continue;

                try {
                    var message = Encoding.ASCII.GetString(result.Buffer);
                    _messages.Add(message);
                } catch {}
            }
        }

        public void StopListening() {
            try {
                _listener.DropMulticastGroup(_multicastAddress);
                _cancellationTokenSource?.Cancel();
                _receiveTask?.Wait(1000);
                _receiveTask = null;
            } catch {}
        }
        
        public void StopListening(int expectedMessageCount) {
            StopListening(expectedMessageCount, new CancellationTokenSource(10000).Token);
        }

        public void StopListening(int expectedMessageCount, CancellationToken cancellationToken) {
            while (_messages.Count < expectedMessageCount) {
                if (cancellationToken.IsCancellationRequested)
                    break;
                if (_cancellationTokenSource.Token.IsCancellationRequested)
                    break;
                
                Thread.Sleep(100);
            }
            
            StopListening();
        }

        public void Dispose() {
            StopListening();
            _listener?.Dispose();
        }
    }
}