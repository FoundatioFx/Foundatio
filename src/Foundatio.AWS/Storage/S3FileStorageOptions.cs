using System;

namespace Foundatio.Storage {
    public class S3FileStorageOptions : SharedOptions {
        public string ConnectionString { get; set; }
    }

    public class S3FileStorageOptionsBuilder : SharedOptionsBuilder<S3FileStorageOptions, S3FileStorageOptionsBuilder> {
        public S3FileStorageOptionsBuilder ConnectionString(string connectionString) {
            if (string.IsNullOrEmpty(connectionString))
                throw new ArgumentNullException(nameof(connectionString));
            Target.ConnectionString = connectionString;
            return this;
        }
    }
}