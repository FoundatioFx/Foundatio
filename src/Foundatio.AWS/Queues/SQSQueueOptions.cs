using System;
using Amazon;
using Amazon.Runtime;

namespace Foundatio.Queues {
    public class SQSQueueOptions<T> : QueueOptions<T> where T : class {
        public AWSCredentials Credentials { get; set; }
        public RegionEndpoint RegionEndpoint { get; set; } = RegionEndpoint.USEast1;
        public bool CanCreateQueue { get; set; } = true;
        public bool SupportDeadLetter { get; set; } = true;
        public TimeSpan ReadQueueTimeout { get; set; } = TimeSpan.FromSeconds(20);
        public TimeSpan DequeueInterval { get; set; } = TimeSpan.FromSeconds(1);
    }
}