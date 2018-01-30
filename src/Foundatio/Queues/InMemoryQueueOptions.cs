using System;
using System.Collections.Generic;
using System.Linq;

namespace Foundatio.Queues {
    public class InMemoryQueueOptions<T> : SharedQueueOptions<T> where T : class {
        public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMinutes(1);
        public int[] RetryMultipliers { get; set; } = { 1, 3, 5, 10 };
    }

    public class InMemoryQueueOptionsBuilder<T> : SharedQueueOptionsBuilder<T, InMemoryQueueOptions<T>, InMemoryQueueOptionsBuilder<T>> where T: class {
        public InMemoryQueueOptionsBuilder<T> RetryDelay(TimeSpan retryDelay) {
            Target.RetryDelay = retryDelay;
            return this;
        }

        public InMemoryQueueOptionsBuilder<T> RetryMultipliers(int[] multipliers) {
            Target.RetryMultipliers = multipliers ?? throw new ArgumentNullException(nameof(multipliers));
            return this;
        }
    }
}