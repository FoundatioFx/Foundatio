using System;
using Microsoft.WindowsAzure.Storage.RetryPolicies;

namespace Foundatio.Queues {
    public class AzureStorageQueueOptions<T> : QueueOptionsBase<T> where T : class {
        public string ConnectionString { get; set; }
        public IRetryPolicy RetryPolicy { get; set; }
        public TimeSpan DequeueInterval { get; set; } = TimeSpan.FromSeconds(1);
    }

    public static class FileStorageOptionsExtensions {
        public static IOptionsBuilder<AzureStorageQueueOptions<T>> ConnectionString<T>(this IOptionsBuilder<AzureStorageQueueOptions<T>> builder, string connectionString) where T: class {
            if (builder == null)
                throw new ArgumentNullException(nameof(builder));
            
            builder.Target.ConnectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
            return builder;
        }

        public static IOptionsBuilder<AzureStorageQueueOptions<T>> ContainerName<T>(this IOptionsBuilder<AzureStorageQueueOptions<T>> builder, IRetryPolicy retryPolicy) where T: class {
            if (builder == null)
                throw new ArgumentNullException(nameof(builder));

            builder.Target.RetryPolicy = retryPolicy ?? throw new ArgumentNullException(nameof(retryPolicy));
            return builder;
        }

        public static IOptionsBuilder<AzureStorageQueueOptions<T>> DequeueInterval<T>(this IOptionsBuilder<AzureStorageQueueOptions<T>> builder, TimeSpan dequeueInterval) where T: class {
            if (builder == null)
                throw new ArgumentNullException(nameof(builder));

            builder.Target.DequeueInterval = dequeueInterval;
            return builder;
        }
    }
}