using System;

namespace Foundatio.Queues {
    public class SQSQueueOptions<T> : QueueOptionsBase<T> where T : class {
        public string ConnectionString { get; set; }
        public bool CanCreateQueue { get; set; } = true;
        public bool SupportDeadLetter { get; set; } = true;
        public TimeSpan ReadQueueTimeout { get; set; } = TimeSpan.FromSeconds(20);
        public TimeSpan DequeueInterval { get; set; } = TimeSpan.FromSeconds(1);
    }

    public static class SQSQueueOptionsExtensions {
        public static SQSQueueOptions<T> WithConnectionString<T>(this SQSQueueOptions<T> options, string connectionString) where T : class {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrEmpty(connectionString))
                throw new ArgumentNullException(nameof(connectionString));
            options.ConnectionString = connectionString;
            return options;
        }

        public static SQSQueueOptions<T> WithReadQueueTimeout<T>(this SQSQueueOptions<T> options, TimeSpan timeout) where T : class {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            options.ReadQueueTimeout = timeout;
            return options;
        }

        public static SQSQueueOptions<T> WithDequeueInterval<T>(this SQSQueueOptions<T> options, TimeSpan interval) where T : class {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            options.DequeueInterval = interval;
            return options;
        }

        public static SQSQueueOptions<T> ShouldCreateQueue<T>(this SQSQueueOptions<T> options, bool enabled) where T : class {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            options.CanCreateQueue = enabled;
            return options;
        }

        public static SQSQueueOptions<T> EnableCreateQueue<T>(this SQSQueueOptions<T> options) where T : class => options.ShouldCreateQueue(true);

        public static SQSQueueOptions<T> DisableCreateQueue<T>(this SQSQueueOptions<T> options) where T : class => options.ShouldCreateQueue(false);

        public static SQSQueueOptions<T> SupportDeadLetter<T>(this SQSQueueOptions<T> options, bool supported) where T : class {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            options.SupportDeadLetter = supported;
            return options;
        }

        public static SQSQueueOptions<T> EnableDeadLetter<T>(this SQSQueueOptions<T> options) where T : class => options.SupportDeadLetter(true);

        public static SQSQueueOptions<T> DisableDeadLetter<T>(this SQSQueueOptions<T> options) where T : class => options.SupportDeadLetter(false);
    }
}