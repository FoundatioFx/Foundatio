using System;
using StackExchange.Redis;

namespace Foundatio.Queues {
    // TODO: Make queue settings immutable and stored in redis so that multiple clients can't have different settings.
    public class RedisQueueOptions<T> : QueueOptionsBase<T> where T : class {
        public ConnectionMultiplexer ConnectionMultiplexer { get; set; }
        public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMinutes(1);
        public int[] RetryMultipliers { get; set; } = { 1, 3, 5, 10 };
        public TimeSpan DeadLetterTimeToLive { get; set; } = TimeSpan.FromDays(1);
        public int DeadLetterMaxItems { get; set; } = 100;
        public bool RunMaintenanceTasks { get; set; } = true;
    }
}