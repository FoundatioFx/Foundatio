using System;
using Amazon;
using Amazon.Runtime;

namespace Foundatio.Storage {
    public class S3FileStorageOptions : FileStorageOptionsBase {
        public AWSCredentials Credentials { get; set; }
        public RegionEndpoint Region { get; set; }
        public string Bucket { get; set; } = "storage";

        public static S3FileStorageOptions Parse(string connectionString) {
            if (String.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentNullException(nameof(connectionString));

            var options = new S3FileStorageOptions();
            string accessKey = null, secretKey = null;
            foreach (string optionText in connectionString.Split(',', ';')) {
                if (String.IsNullOrWhiteSpace(optionText))
                    continue;

                string optionString = optionText.Trim();
                int index = optionString.IndexOf('=');
                if (index <= 0)
                    continue;

                string key = optionString.Substring(0, index).Trim();
                string value = optionString.Substring(index + 1).Trim();

                if (String.Equals(key, "AccessKey", StringComparison.OrdinalIgnoreCase) ||
                    String.Equals(key, "Access Key", StringComparison.OrdinalIgnoreCase) ||
                    String.Equals(key, "Id", StringComparison.OrdinalIgnoreCase)) {
                    accessKey = value;
                }
                else if (String.Equals(key, "SecretKey", StringComparison.OrdinalIgnoreCase) ||
                         String.Equals(key, "Secret Key", StringComparison.OrdinalIgnoreCase) ||
                         String.Equals(key, "Secret", StringComparison.OrdinalIgnoreCase)) {
                    secretKey = value;
                }
                else if (String.Equals(key, "EndPoint", StringComparison.OrdinalIgnoreCase) ||
                         String.Equals(key, "End Point", StringComparison.OrdinalIgnoreCase) ||
                         String.Equals(key, "Region", StringComparison.OrdinalIgnoreCase)) {
                    options.Region = RegionEndpoint.GetBySystemName(value);
                }
                else if (String.Equals(key, "Bucket", StringComparison.OrdinalIgnoreCase)) {
                    options.Bucket = value;
                }
            }
            if (!string.IsNullOrEmpty(accessKey)) {
                options.Credentials = new BasicAWSCredentials(accessKey, secretKey);
            }
            return options;
        }
    }
}