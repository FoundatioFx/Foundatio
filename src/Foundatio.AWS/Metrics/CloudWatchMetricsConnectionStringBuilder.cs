using System;
using System.Collections.Generic;
using Amazon.CloudWatch.Model;

namespace Foundatio.Metrics
{
    public class CloudWatchMetricsConnectionStringBuilder : AmazonConnectionStringBuilder
    {
        private string _namespace;

        public CloudWatchMetricsConnectionStringBuilder()
        {
        }

        public CloudWatchMetricsConnectionStringBuilder(string connectionString) : base(connectionString)
        {
        }

        public string Namespace
        {
            get => string.IsNullOrEmpty(_namespace) ? "app/metrics" : _namespace;
            set => _namespace = value;
        }

        public List<Dimension> Dimensions { get; set; } = new List<Dimension>();

        protected override bool ParseItem(string key, string value)
        {
            if (String.Equals(key, "Namespace", StringComparison.OrdinalIgnoreCase))
                Namespace = value;
            else if (!base.ParseItem(key, value))
                Dimensions.Add(new Dimension
                {
                    Name = key,
                    Value = value
                });
            return true;
        }

        public override string ToString()
        {
            var connectionString = base.ToString();
            if (!string.IsNullOrEmpty(_namespace))
                connectionString += "Namespace=" + _namespace + ";";
            foreach (var dimension in Dimensions)
                connectionString += dimension.Name + "=" + dimension.Value + ";";
            return connectionString;
        }
    }
}
