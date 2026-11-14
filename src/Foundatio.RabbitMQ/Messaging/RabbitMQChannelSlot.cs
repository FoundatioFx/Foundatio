using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using RabbitMQ.Client;

namespace Foundatio.Messaging.Transport;

/// <summary>
/// A channel opened on first use and opened again once it has closed, for example after the broker closed it with
/// an error (a failed declare closes the channel it was sent on) or its connection was lost.
/// </summary>
internal sealed class RabbitMQChannelSlot : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<IChannel>> _open;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IChannel? _channel;
    private bool _disposed;

    public RabbitMQChannelSlot(Func<CancellationToken, Task<IChannel>> open)
    {
        _open = open;
    }

    public async Task<IChannel> GetAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _channel) is { IsOpen: true } open)
            return open;

        await _lock.WaitAsync(cancellationToken).AnyContext();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_channel is { IsOpen: true } current)
                return current;

            if (_channel is not null)
                await CloseAsync(_channel).AnyContext();

            var channel = await _open(cancellationToken).AnyContext();
            Volatile.Write(ref _channel, channel);
            return channel;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lock.WaitAsync().AnyContext();
        try
        {
            if (_disposed)
                return;

            _disposed = true;
            if (_channel is not null)
                await CloseAsync(_channel).AnyContext();

            _channel = null;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static async Task CloseAsync(IChannel channel)
    {
        try
        {
            if (channel.IsOpen)
                await channel.CloseAsync().AnyContext();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The channel or its connection is already gone.
        }

        try
        {
            await channel.DisposeAsync().AnyContext();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }
    }
}
