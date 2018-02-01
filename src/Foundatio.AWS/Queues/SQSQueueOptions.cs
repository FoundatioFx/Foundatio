using System;

namespace Foundatio.Queues {
    public class SQSQueueOptions<T> : SharedQueueOptions<T> where T : class {
        public string ConnectionString { get; set; }
        public bool CanCreateQueue { get; set; } = true;
        public bool SupportDeadLetter { get; set; } = true;
        public TimeSpan ReadQueueTimeout { get; set; } = TimeSpan.FromSeconds(20);
        public TimeSpan DequeueInterval { get; set; } = TimeSpan.FromSeconds(1);
    }

    public class SQSQueueOptionsBuilder<T> : SharedQueueOptionsBuilder<T, SQSQueueOptions<T>, SQSQueueOptionsBuilder<T>> where T : class {
        public SQSQueueOptionsBuilder<T> ConnectionString(string connectionString) {
            if (string.IsNullOrEmpty(connectionString))
                throw new ArgumentNullException(nameof(connectionString));
            Target.ConnectionString = connectionString;
            return this;
        }

        public SQSQueueOptionsBuilder<T> ReadQueueTimeout(TimeSpan timeout) {
            Target.ReadQueueTimeout = timeout;
            return this;
        }

        public SQSQueueOptionsBuilder<T> DequeueInterval(TimeSpan interval) {
            Target.DequeueInterval = interval;
            return this;
        }

        public SQSQueueOptionsBuilder<T> CanCreateQueue(bool enabled) {
            Target.CanCreateQueue = enabled;
            return this;
        }

        public SQSQueueOptionsBuilder<T> EnableCreateQueue() => CanCreateQueue(true);

        public SQSQueueOptionsBuilder<T> DisableCreateQueue() => CanCreateQueue(false);

        public SQSQueueOptionsBuilder<T> SupportDeadLetter(bool supported) {
            Target.SupportDeadLetter = supported;
            return this;
        }

        public SQSQueueOptionsBuilder<T> EnableDeadLetter() => SupportDeadLetter(true);

        public SQSQueueOptionsBuilder<T> DisableDeadLetter() => SupportDeadLetter(false);
    }
}