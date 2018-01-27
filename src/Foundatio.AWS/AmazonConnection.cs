using System;
using System.Linq;
using Amazon;
using Amazon.Runtime;

namespace Foundatio {
    internal class AmazonConnection {
        public AWSCredentials Credentials { get; set; }
        public RegionEndpoint Region { get; set; }

        protected static T Parse<T>(string connectionString, Func<string, string, T, bool> itemParser = null) where T : AmazonConnection, new() {
            var connection = new T();
            if (itemParser == null) {
                itemParser = (key, value, con) => false;
            }
            foreach (var option in connectionString.Split(';').Where(kvp => kvp.Contains('=')).Select(kvp => kvp.Split(new[] { '=' }, 2))) {
                var optionKey = option[0].Trim();
                var optionValue = option[1].Trim();
                string accessKey = null, secretKey = null;
                if (String.Equals(optionKey, "AccessKey", StringComparison.OrdinalIgnoreCase) ||
                    String.Equals(optionKey, "Access Key", StringComparison.OrdinalIgnoreCase) ||
                    String.Equals(optionKey, "Id", StringComparison.OrdinalIgnoreCase)) {
                    accessKey = optionValue;
                } else if (String.Equals(optionKey, "SecretKey", StringComparison.OrdinalIgnoreCase) ||
                           String.Equals(optionKey, "Secret Key", StringComparison.OrdinalIgnoreCase) ||
                           String.Equals(optionKey, "Secret", StringComparison.OrdinalIgnoreCase)) {
                    secretKey = optionValue;
                } else if (String.Equals(optionKey, "EndPoint", StringComparison.OrdinalIgnoreCase) ||
                           String.Equals(optionKey, "End Point", StringComparison.OrdinalIgnoreCase) ||
                           String.Equals(optionKey, "Region", StringComparison.OrdinalIgnoreCase)) {
                    connection.Region = RegionEndpoint.GetBySystemName(optionValue);
                } else if (!itemParser(optionKey, optionValue, connection)) {
                    throw new ArgumentException($"The option '{optionKey}' cannot be recognized in connection string.", nameof(connectionString));
                }
                if (!string.IsNullOrEmpty(accessKey)) {
                    connection.Credentials = new BasicAWSCredentials(accessKey, secretKey);
                }
            }
            return connection;
        }
    }
}
