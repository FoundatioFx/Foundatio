using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.RabbitMQ.Tests;

/// <summary>
/// A local TCP proxy for broker faults: it forwards to a backend until <see cref="StallFromBackend"/> holds back
/// everything the backend sends (the broker takes publishes and never confirms them), or, without a backend, accepts
/// connections and never answers (a node that accepts TCP but never completes the AMQP handshake).
/// </summary>
internal sealed class TcpTestProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly string? _backendHost;
    private readonly int _backendPort;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentBag<IDisposable> _sockets = [];
    private readonly Task _accepting;
    private volatile bool _stalled;
    private int _accepted;

    private TcpTestProxy(string? backendHost, int backendPort)
    {
        _backendHost = backendHost;
        _backendPort = backendPort;
        _listener.Start();
        _accepting = AcceptAsync();
    }

    /// <summary>Forwards to <paramref name="host"/>:<paramref name="port"/>.</summary>
    public static TcpTestProxy To(string host, int port) => new(host, port);

    /// <summary>Accepts connections and never sends anything on them.</summary>
    public static TcpTestProxy Silent() => new(null, 0);

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>"127.0.0.1:port", as a RabbitMQ host.</summary>
    public string Host => $"127.0.0.1:{Port}";

    /// <summary>The connections accepted so far.</summary>
    public int Accepted => Volatile.Read(ref _accepted);

    /// <summary>Stops passing on what the backend sends, holding it, while the client's own traffic still reaches the backend.</summary>
    public void StallFromBackend() => _stalled = true;

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        foreach (var socket in _sockets)
            socket.Dispose();

        try
        {
            await _accepting;
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }

        _stop.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var client = await _listener.AcceptTcpClientAsync(_stop.Token);
            Interlocked.Increment(ref _accepted);
            _sockets.Add(client);
            if (_backendHost is not null)
                _ = ForwardAsync(client);
        }
    }

    private async Task ForwardAsync(TcpClient client)
    {
        try
        {
            var backend = new TcpClient();
            _sockets.Add(backend);
            await backend.ConnectAsync(_backendHost!, _backendPort, _stop.Token);
            var fromClient = PumpAsync(client.GetStream(), backend.GetStream(), stallable: false);
            var fromBackend = PumpAsync(backend.GetStream(), client.GetStream(), stallable: true);
            await Task.WhenAny(fromClient, fromBackend);
            client.Dispose();
            backend.Dispose();
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException or System.IO.IOException)
        {
            client.Dispose();
        }
    }

    private async Task PumpAsync(NetworkStream from, NetworkStream to, bool stallable)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                int read = await from.ReadAsync(buffer, _stop.Token);
                if (read == 0)
                    return;

                while (stallable && _stalled)
                    await Task.Delay(50, _stop.Token);

                await to.WriteAsync(buffer.AsMemory(0, read), _stop.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException or System.IO.IOException)
        {
        }
    }
}
