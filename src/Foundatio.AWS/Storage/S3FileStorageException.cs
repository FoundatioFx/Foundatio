using System;
using System.Collections.Generic;
using System.Text;

namespace Foundatio.AWS.Storage {
    public class S3FileStorageException : Exception {
        public S3FileStorageException() : base() {
        }

        public S3FileStorageException(string message) : base(message) {
        }

        public S3FileStorageException(string message, Exception innerException) : base(message, innerException) {
        }
    }
}
