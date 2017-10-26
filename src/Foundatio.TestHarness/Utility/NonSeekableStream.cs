using System;
using System.IO;

namespace Foundatio.Tests.Utility {
    public class NonSeekableStream : Stream {
        private readonly Stream _stream;

        public NonSeekableStream(Stream stream) {
            _stream = stream;
        }

        public override bool CanRead => _stream.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => _stream.CanWrite;

        public override void Flush() {
            _stream.Flush();
        }

        public override long Length => throw new NotSupportedException();

        public override long Position {
            get => _stream.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) {
            return _stream.Read(buffer, offset, count);
        }

        public override long Seek(long offset, SeekOrigin origin) {
            throw new NotImplementedException();
        }

        public override void SetLength(long value) {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) {
            _stream.Write(buffer, offset, count);
        }

        public override void Close() {
            _stream.Close();
            base.Close();
        }

        protected override void Dispose(bool disposing) {
            _stream.Dispose();
            base.Dispose(disposing);
        }
    }
}