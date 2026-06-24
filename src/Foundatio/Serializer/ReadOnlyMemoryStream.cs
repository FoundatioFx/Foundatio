using System;
using System.IO;

namespace Foundatio.Serializer;

/// <summary>
/// A read-only, seekable <see cref="Stream"/> over a <see cref="ReadOnlyMemory{T}"/> of bytes.
/// </summary>
/// <remarks>
/// Used as a fallback for deserializing a <see cref="ReadOnlyMemory{T}"/> whose backing store is not
/// a managed array (so the no-copy <see cref="System.Runtime.InteropServices.MemoryMarshal.TryGetArray"/>
/// fast path is unavailable). This avoids allocating an intermediate <c>byte[]</c> copy just to wrap
/// the payload in a <see cref="MemoryStream"/>.
/// </remarks>
internal sealed class ReadOnlyMemoryStream : Stream
{
    private readonly ReadOnlyMemory<byte> _memory;
    private int _position;

    public ReadOnlyMemoryStream(ReadOnlyMemory<byte> memory)
    {
        _memory = memory;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _memory.Length;

    public override long Position
    {
        get => _position;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, _memory.Length);
            _position = (int)value;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        int remaining = _memory.Length - _position;
        if (remaining <= 0)
            return 0;

        int toCopy = Math.Min(remaining, buffer.Length);
        _memory.Span.Slice(_position, toCopy).CopyTo(buffer);
        _position += toCopy;
        return toCopy;
    }

    public override int ReadByte()
    {
        if (_position >= _memory.Length)
            return -1;

        return _memory.Span[_position++];
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _memory.Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };

        ArgumentOutOfRangeException.ThrowIfNegative(target);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(target, _memory.Length);
        _position = (int)target;
        return _position;
    }

    public override void Flush() { }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
