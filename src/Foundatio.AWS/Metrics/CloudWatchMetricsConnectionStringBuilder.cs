using System;
using System.Collections.Generic;
using Amazon.CloudWatch.Model;

namespace Foundatio.Metrics {
    internal class CloudWatchMetricsConnectionStringBuilder : AmazonConnectionStringBuilder {
        public CloudWatchMetricsConnectionStringBuilder()
        {
        }

        public CloudWatchMetricsConnectionStringBuilder(string connectionString) : base(connectionString)
        {
        }

        public string Namespace { get; set; } = "app/metrics";

        public List<Dimension> Dimensions { get; set; } = new List<Dimension>();

        protected override bool ParseItem(string key, string value) {
            if (String.Equals(key, "Namespace", StringComparison.OrdinalIgnoreCase))
                Namespace = value;
            if (!base.ParseItem(key, value))
                Dimensions.Add(new Dimension {
                    Name = key,
                    Value = value
                });
            return true;
        }

        public override string ToString() {
            var connectionString = base.ToString();
            if (!string.IsNullOrEmpty(Namespace))
                connectionString += "Namespace=" + Namespace + ";";
            foreach (var dimension in Dimensions)
                connectionString += dimension.Name + "=" + dimension.Value + ";";
            return connectionString;
        }
    }
}
