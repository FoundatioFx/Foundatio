using System;

namespace Foundatio.Storage {
    public class S3FileStorageConnectionStringBuilder : AmazonConnectionStringBuilder {
        private string _bucket;

        public S3FileStorageConnectionStringBuilder() {
        }

        public S3FileStorageConnectionStringBuilder(string connectionString) : base(connectionString) {
        }

        public string Bucket {
            get => string.IsNullOrEmpty(_bucket) ? "storage" : _bucket;
            set => _bucket = value;
        }

        protected override bool ParseItem(string key, string value) {
            if (String.Equals(key, "Bucket", StringComparison.OrdinalIgnoreCase)) {
                Bucket = value;
                return true;
            }
            return base.ParseItem(key, value);
        }

        public override string ToString() {
            var connectionString = base.ToString();
            if (!string.IsNullOrEmpty(_bucket))
                connectionString += "Bucket=" + Bucket + ";";
            return connectionString;
        }
    }
}
