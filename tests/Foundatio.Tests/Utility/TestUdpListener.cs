using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Foundatio.Tests.Utility {
    public class TestUdpListener : IDisposable {
        private readonly List<string> _messages = new();
        private UdpClient _listener;
        private readonly IPEndPoint _localIpEndPoint;
        private IPEndPoint _senderIpEndPoint;
        private readonly ILogger _logger;
        private Task _receiveTask;
        private CancellationTokenSource _cancellationTokenSource;
        private readonly object _lock = new();

        public TestUdpListener(string server, int port, ILoggerFactory loggerFactory) {
            _logger = loggerFactory.CreateLogger<TestUdpListener>();
            _localIpEndPoint = new IPEndPoint(IPAddress.Parse(server), port);
            _senderIpEndPoint = new IPEndPoint(IPAddress.Any, 0);
        }

        public string[] GetMessages() {
            return _messages.ToArray();
        }

        public void ResetMessages() {
            _logger.LogInformation("ResetMessages");
            _messages.Clear();
        }

        public void StartListening() {
            lock (_lock) {
                if (_listener != null) {
                    _logger.LogInformation("StartListening: Already listening");
                    return;
                }

                _logger.LogInformation("StartListening");
                _cancellationTokenSource = new CancellationTokenSource();
                _listener = new UdpClient(_localIpEndPoint) {
                    Client = { ReceiveTimeout = 1000 }
                };
                _receiveTask = Task.Factory.StartNew(() => ProcessMessages(_cancellationTokenSource.Token), _cancellationTokenSource.Token);
            }
        }

        private void ProcessMessages(CancellationToken cancellationToken) {
            while (true) {
                if (cancellationToken.IsCancellationRequested) {
                    _logger.LogInformation("Stopped ProcessMessages due to CancellationToken.IsCancellationRequested");
                    _cancellationTokenSource = null;
                    StopListening();
                    return;
                }

                try {
                    lock (_lock) {
                        if (_listener == null)
                            break;
                        
                        var result = _listener.Receive(ref _senderIpEndPoint);
                        if (result.Length == 0)
                            continue;

                        string message = Encoding.UTF8.GetString(result, 0, result.Length);
                        _logger.LogInformation("Received message: {Message}", message);
                        _messages.Add(message);
                    }
                } catch (Exception ex) {
                    _logger.LogError(ex, "Error during ProcessMessages: {Message}", ex.Message);
                }
            }
        }

        public void StopListening() {
            try {
                lock (_lock) {
                    _logger.LogInformation("Closing listener socket");
                    _listener?.Close();
                    _listener?.Dispose();
                    _listener = null;
                    _cancellationTokenSource?.Cancel();
                    _receiveTask?.Wait(1000);
                    _receiveTask = null;
                }
            } catch (Exception ex) {
                _logger.LogError(ex, "Error during StopListening: {Message}", ex.Message);
            }
        }
        
        public void StopListening(int expectedMessageCount) {
            StopListening(expectedMessageCount, new CancellationTokenSource(10000).Token);
        }

        public void StopListening(int expectedMessageCount, CancellationToken cancellationToken) {
            _logger.LogInformation($"StopListening called, waiting for {expectedMessageCount} expected message(s)");
            while (_messages.Count < expectedMessageCount) {
                if (cancellationToken.IsCancellationRequested) {
                    _logger.LogInformation("Stopped listening due to CancellationToken.IsCancellationRequested");
                    break;
                }
                if (_cancellationTokenSource.Token.IsCancellationRequested) {
                    _logger.LogInformation("Stopped listening due to CancellationTokenSource.IsCancellationRequested");
                    break;
                }
                
                Thread.Sleep(100);
            }
            
            _logger.LogInformation("StopListening Count={Count}", _messages.Count);
            StopListening();
        }

        public void Dispose() {
            _logger.LogInformation("Dispose");
            StopListening();
        }
    }
}