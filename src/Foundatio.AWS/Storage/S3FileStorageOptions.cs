using System;
using Amazon;
using Amazon.Runtime;

namespace Foundatio.Storage {
    public class S3FileStorageOptions : SharedOptions {
        public string ConnectionString { get; set; }
        public string Bucket { get; set; }
        public AWSCredentials Credentials { get; set; }
        public RegionEndpoint Region { get; set; }
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

        public S3FileStorageOptionsBuilder Credentials(AWSCredentials credentials) {
            if (credentials == null)
                throw new ArgumentNullException(nameof(credentials));
            Target.Credentials = credentials;
            return this;
        }

        public S3FileStorageOptionsBuilder Credentials(string accessKey, string secretKey) {
            if (String.IsNullOrEmpty(accessKey))
                throw new ArgumentNullException(nameof(accessKey));
            if (String.IsNullOrEmpty(secretKey))
                throw new ArgumentNullException(nameof(secretKey));
                
            Target.Credentials = new BasicAWSCredentials(accessKey, secretKey);
            return this;
        }

        public S3FileStorageOptionsBuilder Region(RegionEndpoint region) {
            if (region == null)
                throw new ArgumentNullException(nameof(region));
            Target.Region = region;
            return this;
        }

        public S3FileStorageOptionsBuilder Region(string region) {
            if (String.IsNullOrEmpty(region))
                throw new ArgumentNullException(nameof(region));
            Target.Region = RegionEndpoint.GetBySystemName(region);
            return this;
        }

        public override S3FileStorageOptions Build() {
            if (String.IsNullOrEmpty(Target.ConnectionString))
                return Target;
            
            var connectionString = new S3FileStorageConnectionStringBuilder(Target.ConnectionString);
            if (Target.Credentials == null)
                Target.Credentials = connectionString.GetCredentials();

            if (Target.Region == null)
                Target.Region = connectionString.GetRegion();

            if (String.IsNullOrEmpty(Target.Bucket) && !String.IsNullOrEmpty(connectionString.Bucket))
                Target.Bucket = connectionString.Bucket;

            return Target;
        }
    }
}