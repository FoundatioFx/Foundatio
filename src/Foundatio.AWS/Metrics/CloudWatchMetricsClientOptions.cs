using System;
using System.Collections.Generic;
using Amazon;
using Amazon.CloudWatch.Model;
using Amazon.Runtime;

namespace Foundatio.Metrics {
    public class CloudWatchMetricsClientOptions : SharedMetricsClientOptions {
        public string ConnectionString { get; set; }
        public AWSCredentials Credentials { get; set; }
        public RegionEndpoint Region { get; set; }
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

        public CloudWatchMetricsClientOptionsBuilder Credentials(AWSCredentials credentials) {
            if (credentials == null)
                throw new ArgumentNullException(nameof(credentials));
            Target.Credentials = credentials;
            return this;
        }

        public CloudWatchMetricsClientOptionsBuilder Credentials(string accessKey, string secretKey) {
            if (String.IsNullOrEmpty(accessKey))
                throw new ArgumentNullException(nameof(accessKey));
            if (String.IsNullOrEmpty(secretKey))
                throw new ArgumentNullException(nameof(secretKey));
                
            Target.Credentials = new BasicAWSCredentials(accessKey, secretKey);
            return this;
        }

        public CloudWatchMetricsClientOptionsBuilder Region(RegionEndpoint region) {
            if (region == null)
                throw new ArgumentNullException(nameof(region));
            Target.Region = region;
            return this;
        }

        public CloudWatchMetricsClientOptionsBuilder Region(string region) {
            if (String.IsNullOrEmpty(region))
                throw new ArgumentNullException(nameof(region));
            Target.Region = RegionEndpoint.GetBySystemName(region);
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
            if (String.IsNullOrEmpty(Target.ConnectionString))
                return Target;
            
            var connectionString = new CloudWatchMetricsConnectionStringBuilder(Target.ConnectionString);
            if (Target.Credentials == null)
                Target.Credentials = connectionString.GetCredentials();

            if (Target.Region == null)
                Target.Region = connectionString.GetRegion();

            if (String.IsNullOrEmpty(Target.Namespace) && !String.IsNullOrEmpty(connectionString.Namespace))
                Target.Namespace = connectionString.Namespace;

            if (connectionString.Dimensions.Count > 0)
                Target.Dimensions.AddRange(connectionString.Dimensions);

            return Target;
        }
    }
}