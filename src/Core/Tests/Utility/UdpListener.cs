using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Foundatio.Tests.Utility {
    public class UdpListener : IDisposable {
        private readonly List<string> _receivedMessages = new List<string>();
        private readonly string _serverName;
        private readonly int _port;
        private UdpClient _listener;
        private IPEndPoint _groupEndPoint;

        public UdpListener(string serverName, int port) {
            _serverName = serverName;
            _port = port;
            _groupEndPoint = new IPEndPoint(IPAddress.Any, 0);
        }

        public List<string> GetMessages() {
            var result = new List<string>(_receivedMessages);
            _receivedMessages.Clear();

            return result;
        }

        public void StartListening(object expectedMessageCount = null) {
            if (_listener == null)
                _listener = new UdpClient(new IPEndPoint(IPAddress.Parse(_serverName), _port)) { Client = { ReceiveTimeout = 2000 } };
            
            if (expectedMessageCount == null)
                expectedMessageCount = 1;

            for (int index = 0; index < (int)expectedMessageCount; index++) {
                try {
                    byte[] data = _listener.Receive(ref _groupEndPoint);
                    _receivedMessages.Add(Encoding.ASCII.GetString(data, 0, data.Length));
                } catch (SocketException ex) {
                    // If we timeout, stop listening.
                    if (ex.ErrorCode == 10060)
                        continue;

                    throw;
                }
            }
        }

        public void StopListening() {
            if (_listener == null)
                return;

            _listener.Close();
            _listener = null;
        }

        public void Dispose() {
            StopListening();
        }
    }
}