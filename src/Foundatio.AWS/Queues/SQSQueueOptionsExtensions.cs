using System;

namespace Foundatio.Queues {
    public static class SQSQueueOptionsExtensions {
        public static SQSQueueOptions<T> ConnectionString<T>(this SQSQueueOptions<T> options, string connectionString) where T : class {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrEmpty(connectionString))
                throw new ArgumentNullException(nameof(connectionString));
            options.ConnectionString = connectionString;
            return options;
        }

        public static SQSQueueOptions<T> CanCreateQueue<T>(this SQSQueueOptions<T> options, bool enabled) where T : class {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            options.CanCreateQueue = enabled;
            return options;
        }

        public static SQSQueueOptions<T> EnableCreateQueue<T>(this SQSQueueOptions<T> options) where T : class => options.CanCreateQueue(true);

        public static SQSQueueOptions<T> DisableCreateQueue<T>(this SQSQueueOptions<T> options) where T : class => options.CanCreateQueue(false);

        public static SQSQueueOptions<T> SupportDeadLetter<T>(this SQSQueueOptions<T> options, bool enabled) where T : class {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            options.SupportDeadLetter = enabled;
            return options;
        }

        public static SQSQueueOptions<T> EnableDeadLetter<T>(this SQSQueueOptions<T> options) where T : class => options.SupportDeadLetter(true);

        public static SQSQueueOptions<T> DisableDeadLetter<T>(this SQSQueueOptions<T> options) where T : class => options.SupportDeadLetter(false);

        public static SQSQueueOptions<T> ReadQueueTimeout<T>(this SQSQueueOptions<T> options, TimeSpan timeout) where T : class {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            options.ReadQueueTimeout = timeout;
            return options;
        }

        public static SQSQueueOptions<T> DequeueInterval<T>(this SQSQueueOptions<T> options, TimeSpan interval) where T : class {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            options.DequeueInterval = interval;
            return options;
        }
    }
}
