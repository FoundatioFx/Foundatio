using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Foundatio.Tests.Utility {
    public class UdpListener : IDisposable {
        private CancellationTokenSource _cancellationToken = new CancellationTokenSource();
        private readonly object _lock = new object();
        private readonly List<string> _receivedMessages = new List<string>();
        private readonly IPEndPoint _localEndPoint;
        private IPEndPoint _remoteEndPoint;
        private UdpClient _listener;

        public UdpListener(string serverName, int port) {
            _localEndPoint = new IPEndPoint(IPAddress.Parse(serverName), port);
            _remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);
        }

        public List<string> GetMessages() {
            var result = new List<string>(_receivedMessages);
            _receivedMessages.Clear();

            return result;
        }

        public void StartListening(object expectedMessageCount = null) {
            if (_cancellationToken.IsCancellationRequested)
                _cancellationToken = new CancellationTokenSource();

            if (expectedMessageCount == null)
                expectedMessageCount = 1;

            for (int index = 0; index < (int)expectedMessageCount; index++) {
                if (_cancellationToken.IsCancellationRequested)
                    return;

                EnsureListening();

                try {
                    var data = _listener.Receive(ref _remoteEndPoint);
                    _receivedMessages.Add(Encoding.ASCII.GetString(data, 0, data.Length));
                } catch (SocketException ex) when (ex.ErrorCode == 10060) {
                    // If we timeout, stop listening.
                } catch (Exception) {
                    break;
                }
            }
        }

        public void EnsureListening() {
            if (_listener != null)
                return;

            lock(_lock) {
                if (_listener == null)
                    _listener = new UdpClient(_localEndPoint) { Client = { ReceiveTimeout = 2000 } };
            }
        }

        public void StopListening() {
            if (_listener == null)
                return;

            _cancellationToken.Cancel();
            lock (_lock) {
                _listener.Close();
                _listener = null;
            }
        }

        public void Dispose() {
            StopListening();
        }
    }
}