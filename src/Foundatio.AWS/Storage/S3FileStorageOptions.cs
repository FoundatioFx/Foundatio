using System;

namespace Foundatio.Storage {
    public class S3FileStorageOptions : FileStorageOptionsBase {
        public string ConnectionString { get; set; }
    }

    public static class S3FileStorageOptionsExtensions {
        public static S3FileStorageOptions WithConnectionString(this S3FileStorageOptions options, string connectionString) {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrEmpty(connectionString))
                throw new ArgumentNullException(nameof(connectionString));
            options.ConnectionString = connectionString;
            return options;
        }
    }
}