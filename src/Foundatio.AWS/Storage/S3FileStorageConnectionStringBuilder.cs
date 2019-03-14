using System;

namespace Foundatio.Storage {
    public class S3FileStorageConnectionStringBuilder : AmazonConnectionStringBuilder {
        private string _bucket;
        private string _serviceUrl;
        private bool? _useChunkEncoding;

        public S3FileStorageConnectionStringBuilder() {
        }

        public S3FileStorageConnectionStringBuilder(string connectionString) : base(connectionString) {
        }

        public string Bucket {
            get => string.IsNullOrEmpty(_bucket) ? "storage" : _bucket;
            set => _bucket = value;
        }

        public string ServiceUrl {
            get => _serviceUrl;
            set => _serviceUrl = value;
        }

        public bool? UseChunkEncoding {
            get => _useChunkEncoding;
            set => _useChunkEncoding = value;
        }

        protected override bool ParseItem(string key, string value) {
            if (String.Equals(key, "Bucket", StringComparison.OrdinalIgnoreCase)) {
                Bucket = value;
                return true;
            }
            if (String.Equals(key, "UseChunkEncoding", StringComparison.OrdinalIgnoreCase)) {
                _useChunkEncoding = Convert.ToBoolean(value);
                return true;
            }
            if (String.Equals(key, "ServiceUrl", StringComparison.OrdinalIgnoreCase)) {
                _serviceUrl = value;
                return true;
            }

            return base.ParseItem(key, value);
        }

        public override string ToString() {
            var connectionString = base.ToString();
            if (!string.IsNullOrEmpty(_bucket))
                connectionString += "Bucket=" + Bucket + ";";
            if (_useChunkEncoding != null)
                connectionString += "UseChunkEncoding=" + _useChunkEncoding.Value + ";";
            if (!string.IsNullOrEmpty(_serviceUrl))
                connectionString += "ServiceUrl=" + ServiceUrl + ";";

            return connectionString;
        }
    }
}
