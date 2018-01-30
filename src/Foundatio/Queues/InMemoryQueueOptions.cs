using System;
using System.Collections.Generic;
using System.Linq;
using Foundatio.Utility;

namespace Foundatio.Queues {
    public class InMemoryQueueOptions<T> : SharedQueueOptions<T> where T : class {
        public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMinutes(1);
        public int[] RetryMultipliers { get; set; } = { 1, 3, 5, 10 };
    }

    public class InMemoryQueueOptionsBuilder<T> : OptionsBuilder<InMemoryQueueOptions<T>>, ISharedQueueOptionsBuilder where T: class {
        public InMemoryQueueOptionsBuilder<T> RetryDelay(TimeSpan retryDelay) {
            Target.RetryDelay = retryDelay;
            return this;
        }

        public InMemoryQueueOptionsBuilder<T> RetryMultipliers(int[] multipliers) {
            Target.RetryMultipliers = multipliers ?? throw new ArgumentNullException(nameof(multipliers));
            return this;
        }

        public InMemoryQueueOptionsBuilder<T> Behavior(params IQueueBehavior<T>[] behaviors) {
           Target.Behaviors.AddRange(behaviors);
           return this;
        }
    }
}