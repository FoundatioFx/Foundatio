using System;

namespace Foundatio.Queues {
    public class InMemoryQueueOptions<T> : QueueOptionsBase<T> where T : class {
        public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMinutes(1);
        public int[] RetryMultipliers { get; set; } = { 1, 3, 5, 10 };
    }

    public static class InMemoryQueueOptionsExtensions {
        public static IOptionsBuilder<InMemoryQueueOptions<T>> RetryDelay<T>(this IOptionsBuilder<InMemoryQueueOptions<T>> builder, TimeSpan retryDelay) where T : class {
            if (builder == null)
                throw new ArgumentNullException(nameof(builder));
            builder.Target.RetryDelay = retryDelay;
            return builder;
        }

        public static IOptionsBuilder<InMemoryQueueOptions<T>> RetryMultipliers<T>(this IOptionsBuilder<InMemoryQueueOptions<T>> builder, int[] multipliers) where T : class {
            if (builder == null)
                throw new ArgumentNullException(nameof(builder));
            builder.Target.RetryMultipliers = multipliers ?? throw new ArgumentNullException(nameof(multipliers));
            return builder;
        }
    }
}