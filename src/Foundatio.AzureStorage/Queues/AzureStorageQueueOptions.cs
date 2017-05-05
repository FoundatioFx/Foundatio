using System;
using Microsoft.WindowsAzure.Storage.RetryPolicies;

namespace Foundatio.Queues {
    public class AzureStorageQueueOptions<T> : QueueOptionsBase<T> where T : class {
        public string ConnectionString { get; set; }
        public IRetryPolicy RetryPolicy { get; set; }
        public TimeSpan DequeueInterval { get; set; } = TimeSpan.FromSeconds(1);
    }
}