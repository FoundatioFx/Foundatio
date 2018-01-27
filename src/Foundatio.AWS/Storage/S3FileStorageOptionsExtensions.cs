using System;

namespace Foundatio.Storage {
    public static class S3FileStorageOptionsExtensions {
        public static S3FileStorageOptions ConnectionString(this S3FileStorageOptions options, string connectionString) {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrEmpty(connectionString)) 
                throw new ArgumentNullException(nameof(connectionString));
            options.ConnectionString = connectionString;
            return options;
        }
    }
}
