using System;
using System.Collections.Generic;
using Foundatio.Logging;
using Foundatio.Serializer;

namespace Foundatio.Queues {
    public interface IQueueOptions { }

    public class QueueOptions<T> : IQueueOptions  where T : class {
        public string Name { get; set; } = typeof(T).Name;
        public int Retries { get; set; } = 2;
        public TimeSpan WorkItemTimeout { get; set; } = TimeSpan.FromMinutes(5);
        public IEnumerable<IQueueBehavior<T>> Behaviors { get; set; }
        public ISerializer Serializer { get; set; }
        public ILoggerFactory LoggerFactory { get; set; }
    }
}