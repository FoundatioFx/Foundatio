using System;
using System.IO;

namespace Foundatio.Storage {
    public class ActionableStream : Stream {
        private readonly Action _disposeAction;
        private readonly Stream _stream;

        protected override void Dispose(bool disposing) {
            try {
                _disposeAction.Invoke();
            } catch { /* ignore if these are already disposed;  this is to make sure they are */ }

            base.Dispose(disposing);
        }

        public ActionableStream(Stream stream, Action disposeAction) {
            _stream = stream ?? throw new ArgumentNullException();
            _disposeAction = disposeAction;
        }

        public override bool CanRead => _stream.CanRead;

        public override bool CanSeek => _stream.CanSeek;

        public override bool CanWrite => _stream.CanWrite;

        public override void Flush() {
            _stream.Flush();
        }

        public override long Length => _stream.Length;

        public override long Position {
            get => _stream.Position;
            set => _stream.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) {
            return _stream.Read(buffer, offset, count);
        }

        public override long Seek(long offset, SeekOrigin origin) {
            return _stream.Seek(offset, origin);
        }

        public override void SetLength(long value) {
            _stream.SetLength(value);
        }

        public override void Write(byte[] buffer, int offset, int count) {
            _stream.Write(buffer, offset, count);
        }
    }
}
