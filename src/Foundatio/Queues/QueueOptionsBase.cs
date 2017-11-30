using System;
using System.Collections.Generic;
using Foundatio.Serializer;
using Microsoft.Extensions.Logging;

namespace Foundatio.Queues {
    public abstract class QueueOptionsBase<T>  where T : class {
        public string Name { get; set; } = typeof(T).Name;
        public int Retries { get; set; } = 2;
        public TimeSpan WorkItemTimeout { get; set; } = TimeSpan.FromMinutes(5);
        public IEnumerable<IQueueBehavior<T>> Behaviors { get; set; }
        public ISerializer Serializer { get; set; }
        public ILoggerFactory LoggerFactory { get; set; }
    }
}