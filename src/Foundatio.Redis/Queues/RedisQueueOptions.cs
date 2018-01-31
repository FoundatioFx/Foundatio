using System;
using StackExchange.Redis;

namespace Foundatio.Queues {
    // TODO: Make queue settings immutable and stored in redis so that multiple clients can't have different settings.
    public class RedisQueueOptions<T> : SharedQueueOptions<T> where T : class {
        public ConnectionMultiplexer ConnectionMultiplexer { get; set; }
        public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMinutes(1);
        public int[] RetryMultipliers { get; set; } = { 1, 3, 5, 10 };
        public TimeSpan DeadLetterTimeToLive { get; set; } = TimeSpan.FromDays(1);
        public int DeadLetterMaxItems { get; set; } = 100;
        public bool RunMaintenanceTasks { get; set; } = true;
    }

    public class RedisQueueOptionsBuilder<T> : SharedQueueOptionsBuilder<T, RedisQueueOptions<T>, RedisQueueOptionsBuilder<T>> where T: class {
        public RedisQueueOptionsBuilder<T> ConnectionMultiplexer(ConnectionMultiplexer connectionMultiplexer) {
            Target.ConnectionMultiplexer = connectionMultiplexer;
            return this;
        }

        public RedisQueueOptionsBuilder<T> RetryDelay(TimeSpan retryDelay) {
            Target.RetryDelay = retryDelay;
            return this;
        }

        public RedisQueueOptionsBuilder<T> RetryMultipliers(int[] retryMultipliers) {
            Target.RetryMultipliers = retryMultipliers;
            return this;
        }

        public RedisQueueOptionsBuilder<T> DeadLetterTimeToLive(TimeSpan deadLetterTimeToLive) {
            Target.DeadLetterTimeToLive = deadLetterTimeToLive;
            return this;
        }

        public RedisQueueOptionsBuilder<T> DeadLetterMaxItems(int deadLetterMaxItems) {
            Target.DeadLetterMaxItems = deadLetterMaxItems;
            return this;
        }

        public RedisQueueOptionsBuilder<T> RunMaintenanceTasks(bool runMaintenanceTasks) {
            Target.RunMaintenanceTasks = runMaintenanceTasks;
            return this;
        }
    }
}