using System;

namespace Foundatio.Queues {
    public class InMemoryQueueOptions<T> : QueueOptionsBase<T> where T : class {
        public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMinutes(1);
        public int[] RetryMultipliers { get; set; } = { 1, 3, 5, 10 };
    }

    public static class InMemoryQueueOptionsExtensions {
        public static InMemoryQueueOptions<T> WithRetryDelay<T>(this InMemoryQueueOptions<T> options, TimeSpan retryDelay) where T : class {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            options.RetryDelay = retryDelay;
            return options;
        }

        public static InMemoryQueueOptions<T> WithRetryMultipliers<T>(this InMemoryQueueOptions<T> options, int[] multipliers) where T : class {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            options.RetryMultipliers = multipliers ?? throw new ArgumentNullException(nameof(multipliers));
            return options;
        }
    }
}