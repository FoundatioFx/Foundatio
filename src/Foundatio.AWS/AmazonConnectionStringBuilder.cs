using System;
using System.Linq;
using Amazon;
using Amazon.Runtime;

namespace Foundatio {
    public abstract class AmazonConnectionStringBuilder {
        public string AccessKey { get; set; }

        public string SecretKey { get; set; }

        public string Region { get; set; }

        public string ServiceUrl { get; set; }

        protected AmazonConnectionStringBuilder() { }

        protected AmazonConnectionStringBuilder(string connectionString) {
            if (String.IsNullOrEmpty(connectionString))
                throw new ArgumentNullException(nameof(connectionString));

            Parse(connectionString);
        }

        private void Parse(string connectionString) {
            foreach (var option in connectionString
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(kvp => kvp.Contains('='))
                .Select(kvp => kvp.Split(new[] { '=' }, 2))) {
                string optionKey = option[0].Trim();
                string optionValue = option[1].Trim();
                if (!ParseItem(optionKey, optionValue))
                    throw new ArgumentException($"The option '{optionKey}' cannot be recognized in connection string.", nameof(connectionString));
            }
        }

        public AWSCredentials GetCredentials() {
            return !String.IsNullOrEmpty(AccessKey)
                ? new BasicAWSCredentials(AccessKey, SecretKey)
                : null;
        }

        public RegionEndpoint GetRegion() {
            return !String.IsNullOrEmpty(Region)
                ? RegionEndpoint.GetBySystemName(Region)
                : null;
        }

        protected virtual bool ParseItem(string key, string value) {
            if (String.Equals(key, "AccessKey", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(key, "Access Key", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(key, "AccessKeyId", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(key, "Access Key Id", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(key, "Id", StringComparison.OrdinalIgnoreCase)) {
                AccessKey = value;
                return true;
            }
            if (String.Equals(key, "SecretKey", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(key, "Secret Key", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(key, "SecretAccessKey", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(key, "Secret Access Key", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(key, "Secret", StringComparison.OrdinalIgnoreCase)) {
                SecretKey = value;
                return true;
            }
            if (String.Equals(key, "EndPoint", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(key, "End Point", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(key, "Region", StringComparison.OrdinalIgnoreCase)) {
                Region = value;
                return true;
            }
            if (String.Equals(key, "Service", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(key, "Service Url", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(key, "ServiceUrl", StringComparison.OrdinalIgnoreCase)) {
                ServiceUrl = value;
                return true;
            }
            return false;
        }

        public override string ToString() {
            string connectionString = String.Empty;
            if (!String.IsNullOrEmpty(AccessKey))
                connectionString += "AccessKey=" + AccessKey + ";";
            if (!String.IsNullOrEmpty(SecretKey))
                connectionString += "SecretKey=" + SecretKey + ";";
            if (!String.IsNullOrEmpty(Region))
                connectionString += "Region=" + Region + ";";
            if (!String.IsNullOrEmpty(ServiceUrl))
                connectionString += "ServiceUrl=" + ServiceUrl + ";";
            return connectionString;
        }
    }
}
