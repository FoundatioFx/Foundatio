using System;
using System.Collections.Generic;
using Amazon;
using Amazon.CloudWatch.Model;
using Amazon.Runtime;

namespace Foundatio.Metrics {
    public class CloudWatchMetricsClientOptions : MetricsClientOptionsBase {
        public AWSCredentials Credentials { get; set; }
        public RegionEndpoint RegionEndpoint { get; set; } = RegionEndpoint.USEast1;
        public string Namespace { get; set; } = "app/metrics";
        public List<Dimension> Dimensions { get; set; } = new List<Dimension>();
    }
}