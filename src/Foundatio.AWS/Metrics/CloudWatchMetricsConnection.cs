using System;
using System.Collections.Generic;
using Amazon.CloudWatch.Model;

namespace Foundatio.Metrics {
    internal class CloudWatchMetricsConnection : AmazonConnection {
        public string Namespace { get; set; } = "app/metrics";
        public List<Dimension> Dimensions { get; set; } = new List<Dimension>();

        public static CloudWatchMetricsConnection Parse(string connectionString) {
            return Parse<CloudWatchMetricsConnection>(connectionString, (key, value, connection) => {
                if (String.Equals(key, "Namespace", StringComparison.OrdinalIgnoreCase)) {
                    connection.Namespace = value;
                } else {
                    connection.Dimensions.Add(new Dimension {
                        Name = key,
                        Value = value
                    });
                }
                return true;
            });
        }
    }
}
