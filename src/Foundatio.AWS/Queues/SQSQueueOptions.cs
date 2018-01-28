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
        public static IOptionsBuilder<SQSQueueOptions<T>> ConnectionString<T>(this IOptionsBuilder<SQSQueueOptions<T>> options, string connectionString) where T : class {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrEmpty(connectionString))
                throw new ArgumentNullException(nameof(connectionString));
            options.Target.ConnectionString = connectionString;
            return options;
        }

        public static IOptionsBuilder<SQSQueueOptions<T>> ReadQueueTimeout<T>(this IOptionsBuilder<SQSQueueOptions<T>> options, TimeSpan timeout) where T : class {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            options.Target.ReadQueueTimeout = timeout;
            return options;
        }

        public static IOptionsBuilder<SQSQueueOptions<T>> DequeueInterval<T>(this IOptionsBuilder<SQSQueueOptions<T>> options, TimeSpan interval) where T : class {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            options.Target.DequeueInterval = interval;
            return options;
        }

        public static IOptionsBuilder<SQSQueueOptions<T>> CanCreateQueue<T>(this IOptionsBuilder<SQSQueueOptions<T>> options, bool enabled) where T : class {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            options.Target.CanCreateQueue = enabled;
            return options;
        }

        public static IOptionsBuilder<SQSQueueOptions<T>> EnableCreateQueue<T>(this IOptionsBuilder<SQSQueueOptions<T>> options) where T : class => options.CanCreateQueue(true);

        public static IOptionsBuilder<SQSQueueOptions<T>> DisableCreateQueue<T>(this IOptionsBuilder<SQSQueueOptions<T>> options) where T : class => options.CanCreateQueue(false);

        public static IOptionsBuilder<SQSQueueOptions<T>> SupportDeadLetter<T>(this IOptionsBuilder<SQSQueueOptions<T>> options, bool supported) where T : class {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            options.Target.SupportDeadLetter = supported;
            return options;
        }

        public static IOptionsBuilder<SQSQueueOptions<T>> EnableDeadLetter<T>(this IOptionsBuilder<SQSQueueOptions<T>> options) where T : class => options.SupportDeadLetter(true);

        public static IOptionsBuilder<SQSQueueOptions<T>> DisableDeadLetter<T>(this IOptionsBuilder<SQSQueueOptions<T>> options) where T : class => options.SupportDeadLetter(false);
    }
}