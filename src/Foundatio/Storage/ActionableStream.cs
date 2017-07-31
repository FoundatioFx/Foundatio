using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Storage {
    public class ActionableStream : Stream {
        private readonly Action _disposeAction;
        private readonly Stream _stream;

        protected override void Dispose(bool disposing) {
            try {
                _disposeAction.Invoke();
            } catch { /* ignore if these are already disposed;  this is to make sure they are */ }
            
            _stream.Dispose();
            base.Dispose(disposing);
        }

        public ActionableStream(Stream stream, Action disposeAction) {
            _stream = stream ?? throw new ArgumentNullException();
            _disposeAction = disposeAction;
        }

        public override bool CanRead => _stream.CanRead;

        public override bool CanSeek => _stream.CanSeek;

        public override bool CanWrite => _stream.CanWrite;

        public override long Length => _stream.Length;

        public override long Position {
            get => _stream.Position;
            set => _stream.Position = value;
        }

        public override long Seek(long offset, SeekOrigin origin) {
            return _stream.Seek(offset, origin);
        }

        public override void SetLength(long value) {
            _stream.SetLength(value);
        }

        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken) {
            return _stream.CopyToAsync(destination, bufferSize, cancellationToken);
        }

        public override void Flush() {
            _stream.Flush();
        }

        public override Task FlushAsync(CancellationToken cancellationToken) {
            return _stream.FlushAsync(cancellationToken);
        }

        public override int Read(byte[] buffer, int offset, int count) {
            return _stream.Read(buffer, offset, count);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) {
            return _stream.ReadAsync(buffer, offset, count, cancellationToken);
        }

        public override void Write(byte[] buffer, int offset, int count) {
            _stream.Write(buffer, offset, count);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) {
            return _stream.WriteAsync(buffer, offset, count, cancellationToken);
        }
    }
}
