using System;

namespace Foundatio.Storage {
    internal class S3FileStorageConnection : AmazonConnection {
        public string Bucket { get; set; } = "storage";

        public static S3FileStorageConnection Parse(string connectionString) {
            return Parse<S3FileStorageConnection>(connectionString, (key, value, connection) => {
                if (String.Equals(key, "Bucket", StringComparison.OrdinalIgnoreCase)) {
                    connection.Bucket = value;
                    return true;
                }
                return false;
            });
        }
    }
}
