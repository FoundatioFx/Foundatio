using System;
using Amazon.S3;

namespace Foundatio.Storage {
    public class S3FileStorageConnectionStringBuilder : AmazonConnectionStringBuilder {
        private string _bucket;
        private string _serviceUrl;
        private string _useChunkEncoding;
        private string _cannedAcl;

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
            get => string.IsNullOrEmpty(_useChunkEncoding) ? null : (bool?)Convert.ToBoolean(_useChunkEncoding);
            set => _useChunkEncoding = value.ToString();
        }

        public S3CannedACL CannedACL {
            get => string.IsNullOrEmpty(_cannedAcl) ? null : S3CannedACL.FindValue(_cannedAcl);
            set => _cannedAcl = value.Value;
        }

        protected override bool ParseItem(string key, string value) {
            if (String.Equals(key, "Bucket", StringComparison.OrdinalIgnoreCase)) {
                Bucket = value;
                return true;
            }
            if (String.Equals(key, "UseChunkEncoding", StringComparison.OrdinalIgnoreCase)) {
                _useChunkEncoding = value;
                return true;
            }
            if (String.Equals(key, "ServiceUrl", StringComparison.OrdinalIgnoreCase)) {
                _serviceUrl = value;
                return true;
            }
            if (String.Equals(key, "CannedACL", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(key, "S3CannedACL", StringComparison.OrdinalIgnoreCase)) {
                _cannedAcl = value;
                return true;
            }

            return base.ParseItem(key, value);
        }

        public override string ToString() {
            var connectionString = base.ToString();
            if (!string.IsNullOrEmpty(_bucket))
                connectionString += "Bucket=" + Bucket + ";";
            if (!string.IsNullOrEmpty(_useChunkEncoding))
                connectionString += "UseChunkEncoding=" + UseChunkEncoding + ";";
            if (!string.IsNullOrEmpty(_serviceUrl))
                connectionString += "ServiceUrl=" + ServiceUrl + ";";
            if (!string.IsNullOrEmpty(_cannedAcl))
                connectionString += "CannedACL=" + CannedACL + ";";

            return connectionString;
        }
    }
}
