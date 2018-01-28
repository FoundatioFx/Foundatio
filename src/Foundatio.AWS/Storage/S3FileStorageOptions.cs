using System;

namespace Foundatio.Storage {
    public class S3FileStorageOptions : FileStorageOptionsBase {
        public string ConnectionString { get; set; }
    }

    public static class S3FileStorageOptionsExtensions {
        public static IOptionsBuilder<S3FileStorageOptions> ConnectionString(this IOptionsBuilder<S3FileStorageOptions> options, string connectionString) {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrEmpty(connectionString))
                throw new ArgumentNullException(nameof(connectionString));
            options.Target.ConnectionString = connectionString;
            return options;
        }
    }
}