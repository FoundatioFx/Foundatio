using System;

namespace Foundatio.Queues {
    public class InMemoryQueueOptions<T> : QueueOptionsBase<T> where T : class {
        public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMinutes(1);
        public int[] RetryMultipliers { get; set; } = { 1, 3, 5, 10 };
    }
}