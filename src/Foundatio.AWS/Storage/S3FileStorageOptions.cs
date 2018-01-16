using System;
using Amazon;
using Amazon.Runtime;

namespace Foundatio.Storage {
    public class S3FileStorageOptions : FileStorageOptionsBase {
        public AWSCredentials Credentials { get; set; }
        public RegionEndpoint Region { get; set; }
        public string Bucket { get; set; } = "storage";
    }
}