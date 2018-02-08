using System;

namespace Foundatio.Storage {
    public class S3FileStorageOptions : SharedOptions {
        public string ConnectionString { get; set; }
        public string Bucket { get; set; }
        public string AccessKey { get; set; }
        public string SecretKey { get; set; }
        public string Region { get; set; }
    }

    public class S3FileStorageOptionsBuilder : SharedOptionsBuilder<S3FileStorageOptions, S3FileStorageOptionsBuilder> {
        public S3FileStorageOptionsBuilder ConnectionString(string connectionString) {
            if (string.IsNullOrEmpty(connectionString))
                throw new ArgumentNullException(nameof(connectionString));
            Target.ConnectionString = connectionString;
            return this;
        }

        public S3FileStorageOptionsBuilder Bucket(string bucket) {
            if (string.IsNullOrEmpty(bucket))
                throw new ArgumentNullException(nameof(bucket));
            Target.Bucket = bucket;
            return this;
        }

        public S3FileStorageOptionsBuilder AccessKey(string accessKey) {
            if (string.IsNullOrEmpty(accessKey))
                throw new ArgumentNullException(nameof(accessKey));
            Target.AccessKey = accessKey;
            return this;
        }

        public S3FileStorageOptionsBuilder SecretKey(string secretKey) {
            if (string.IsNullOrEmpty(secretKey))
                throw new ArgumentNullException(nameof(secretKey));
            Target.SecretKey = secretKey;
            return this;
        }

        public S3FileStorageOptionsBuilder Region(string region) {
            if (string.IsNullOrEmpty(region))
                throw new ArgumentNullException(nameof(region));
            Target.Region = region;
            return this;
        }

        public override S3FileStorageOptions Build() {
            if (String.IsNullOrEmpty(Target.ConnectionString))
                return Target;
            
            var connectionString = new S3FileStorageConnectionStringBuilder(Target.ConnectionString);
            if (String.IsNullOrEmpty(Target.AccessKey) && !String.IsNullOrEmpty(connectionString.AccessKey))
                Target.AccessKey = connectionString.AccessKey;

            if (String.IsNullOrEmpty(Target.SecretKey) && !String.IsNullOrEmpty(connectionString.SecretKey))
                Target.SecretKey = connectionString.SecretKey;

            if (String.IsNullOrEmpty(Target.Bucket) && !String.IsNullOrEmpty(connectionString.Bucket))
                Target.Bucket = connectionString.Bucket;

            if (String.IsNullOrEmpty(Target.Region) && !String.IsNullOrEmpty(connectionString.Region))
                Target.Region = connectionString.Region;

            return Target;
        }
    }
}