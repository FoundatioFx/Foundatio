using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;

namespace Foundatio.Storage;

public class ActionableStream : Stream, IAsyncDisposable
{
    private readonly Func<Task> _disposeAction;
    private readonly Stream _stream;
    private bool _disposed;

    public ActionableStream(Stream stream, Action disposeAction)
    {
        _stream = stream ?? throw new ArgumentNullException();
        _disposeAction = () =>
        {
            disposeAction();
            return Task.CompletedTask;
        };
    }

    public ActionableStream(Stream stream, Func<Task> disposeAction)
    {
        _stream = stream ?? throw new ArgumentNullException();
        _disposeAction = disposeAction;
    }

    public override bool CanRead => _stream.CanRead;

    public override bool CanSeek => _stream.CanSeek;

    public override bool CanWrite => _stream.CanWrite;

    public override long Length => _stream.Length;

    public override long Position
    {
        get => _stream.Position;
        set => _stream.Position = value;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        return _stream.Seek(offset, origin);
    }

    public override void SetLength(long value)
    {
        _stream.SetLength(value);
    }

    public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
    {
        return _stream.CopyToAsync(destination, bufferSize, cancellationToken);
    }

    public override void Flush()
    {
        _stream.Flush();
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        return _stream.FlushAsync(cancellationToken);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return _stream.Read(buffer, offset, count);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return _stream.ReadAsync(buffer, offset, count, cancellationToken);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        _stream.Write(buffer, offset, count);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return _stream.WriteAsync(buffer, offset, count, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        DisposeAsync().GetAwaiter().GetResult();
    }

#if NETSTANDARD
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        try
        {
            _disposed = true;
            await _disposeAction.Invoke().AnyContext();
        }
        catch (ObjectDisposedException)
        {
            /* ignore if these are already disposed; this is to make sure they are */
        }

        if (_stream is IAsyncDisposable streamAsyncDisposable)
        {
            await streamAsyncDisposable.DisposeAsync();
        }
        else
        {
            _stream?.Dispose();
        }

        GC.SuppressFinalize(this);
    }
#else
    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        try
        {
            _disposed = true;
            await _disposeAction.Invoke().AnyContext();
        }
        catch (ObjectDisposedException)
        {
            /* ignore if these are already disposed; this is to make sure they are */
        }

        await base.DisposeAsync().AnyContext();
    }
#endif
}
