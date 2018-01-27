using System;

namespace Foundatio.Queues {
    public class SQSQueueOptions<T> : QueueOptionsBase<T> where T : class {
        public string ConnectionString { get; set; }
        public bool CanCreateQueue { get; set; } = true;
        public bool SupportDeadLetter { get; set; } = true;
        public TimeSpan ReadQueueTimeout { get; set; } = TimeSpan.FromSeconds(20);
        public TimeSpan DequeueInterval { get; set; } = TimeSpan.FromSeconds(1);
    }
}