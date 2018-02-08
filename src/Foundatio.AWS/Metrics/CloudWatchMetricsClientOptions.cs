using System;
using System.Collections.Generic;
using Amazon.CloudWatch.Model;

namespace Foundatio.Metrics {
    public class CloudWatchMetricsClientOptions : SharedMetricsClientOptions {
        public string ConnectionString { get; set; }
        public string AccessKey { get; set; }
        public string SecretKey { get; set; }
        public string Region { get; set; }
        public string Namespace { get; set; }
        public List<Dimension> Dimensions { get; set; } = new List<Dimension>();
    }

    public class CloudWatchMetricsClientOptionsBuilder : SharedMetricsClientOptionsBuilder<CloudWatchMetricsClientOptions, CloudWatchMetricsClientOptionsBuilder> {
        public CloudWatchMetricsClientOptionsBuilder ConnectionString(string connectionString) {
            if (String.IsNullOrEmpty(connectionString))
                throw new ArgumentNullException(nameof(connectionString));
            Target.ConnectionString = connectionString;
            return this;
        }

        public CloudWatchMetricsClientOptionsBuilder AccessKey(string accessKey) {
            if (String.IsNullOrEmpty(accessKey))
                throw new ArgumentNullException(nameof(accessKey));
            Target.AccessKey = accessKey;
            return this;
        }

        public CloudWatchMetricsClientOptionsBuilder SecretKey(string secretKey) {
            if (String.IsNullOrEmpty(secretKey))
                throw new ArgumentNullException(nameof(secretKey));
            Target.SecretKey = secretKey;
            return this;
        }

        public CloudWatchMetricsClientOptionsBuilder Region(string region) {
            if (String.IsNullOrEmpty(region))
                throw new ArgumentNullException(nameof(region));
            Target.Region = region;
            return this;
        }

        public CloudWatchMetricsClientOptionsBuilder Namespace(string value) {
            if (String.IsNullOrEmpty(value))
                throw new ArgumentNullException(nameof(value));
            Target.Namespace = value;
            return this;
        }

        public CloudWatchMetricsClientOptionsBuilder Dimensions(params Dimension[] dimensions) {
            if (dimensions == null)
                throw new ArgumentNullException(nameof(dimensions));
            Target.Dimensions = new List<Dimension>(dimensions);
            return this;
        }

        public CloudWatchMetricsClientOptionsBuilder AddDimension(Dimension dimension) {
            if (dimension == null)
                throw new ArgumentNullException(nameof(dimension));
            Target.Dimensions.Add(dimension);
            return this;
        }

        public CloudWatchMetricsClientOptionsBuilder AddDimension(string key, string value) {
            if (String.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key));
            if (String.IsNullOrEmpty(value))
                throw new ArgumentNullException(nameof(value));
            
            Target.Dimensions.Add(new Dimension {
                    Name = key,
                    Value = value
                });
            return this;
        }

        public override CloudWatchMetricsClientOptions Build() {
            var connectionString = new CloudWatchMetricsConnectionStringBuilder(Target.ConnectionString);
            if (String.IsNullOrEmpty(Target.AccessKey) && !String.IsNullOrEmpty(connectionString.AccessKey))
                Target.AccessKey = connectionString.AccessKey;

            if (String.IsNullOrEmpty(Target.SecretKey) && !String.IsNullOrEmpty(connectionString.SecretKey))
                Target.SecretKey = connectionString.SecretKey;

            if (String.IsNullOrEmpty(Target.Region) && !String.IsNullOrEmpty(connectionString.Region))
                Target.Region = connectionString.Region;

            if (String.IsNullOrEmpty(Target.Namespace) && !String.IsNullOrEmpty(connectionString.Namespace))
                Target.Namespace = connectionString.Namespace;

            if (connectionString.Dimensions.Count > 0)
                Target.Dimensions.AddRange(connectionString.Dimensions);

            return Target;
        }
    }
}