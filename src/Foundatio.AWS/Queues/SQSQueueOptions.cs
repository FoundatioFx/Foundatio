using System;

namespace Foundatio.Queues {
    public class SQSQueueOptions {
        public bool CanCreateQueue { get; set; } = true;
        public bool SupportDeadLetter { get; set; } = true;
        public int RetryCount { get; set; } = 5;
        public TimeSpan WorkItemTimeout { get; set; } = TimeSpan.FromMinutes(5);
        public TimeSpan ReadQueueTimeout { get; set; } = TimeSpan.FromSeconds(2);
        public TimeSpan DequeueInterval { get; set; } = TimeSpan.FromSeconds(1);
    }
}
