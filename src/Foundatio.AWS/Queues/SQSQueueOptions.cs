using System;

namespace Foundatio.Queues {
    public class SQSQueueOptions<T> : SharedQueueOptions<T> where T : class {
        public string ConnectionString { get; set; }
        public string AccessKey { get; set; }
        public string SecretKey { get; set; }
        public string Region { get; set; }
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

        public SQSQueueOptionsBuilder<T> AccessKey(string accessKey) {
            if (string.IsNullOrEmpty(accessKey))
                throw new ArgumentNullException(nameof(accessKey));
            Target.AccessKey = accessKey;
            return this;
        }

        public SQSQueueOptionsBuilder<T> SecretKey(string secretKey) {
            if (string.IsNullOrEmpty(secretKey))
                throw new ArgumentNullException(nameof(secretKey));
            Target.SecretKey = secretKey;
            return this;
        }

        public SQSQueueOptionsBuilder<T> Region(string region) {
            if (string.IsNullOrEmpty(region))
                throw new ArgumentNullException(nameof(region));
            Target.Region = region;
            return this;
        }

        public override SQSQueueOptions<T> Build() {
            var connectionString = new SQSQueueConnectionStringBuilder(Target.ConnectionString);
            if (String.IsNullOrEmpty(Target.AccessKey) && !String.IsNullOrEmpty(connectionString.AccessKey))
                Target.AccessKey = connectionString.AccessKey;

            if (String.IsNullOrEmpty(Target.SecretKey) && !String.IsNullOrEmpty(connectionString.SecretKey))
                Target.SecretKey = connectionString.SecretKey;

            if (String.IsNullOrEmpty(Target.Region) && !String.IsNullOrEmpty(connectionString.Region))
                Target.Region = connectionString.Region;

            return Target;
        }
    }
}