using System;
using System.Collections.Generic;
using System.Linq;
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

    public static class QueueOptionsExtensions {
        public static QueueOptionsBase<T> WithName<T>(this QueueOptionsBase<T> options, string name) where T : class {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrEmpty(name))
                throw new ArgumentNullException(nameof(name));
            options.Name = name;
            return options;
        }

        public static QueueOptionsBase<T> WithRetries<T>(this QueueOptionsBase<T> options, int retries) where T : class {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            options.Retries = retries;
            return options;
        }

        public static QueueOptionsBase<T> WithWorkItemTimeout<T>(this QueueOptionsBase<T> options, TimeSpan timeout) where T : class {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            options.WorkItemTimeout = timeout;
            return options;
        }

        public static QueueOptionsBase<T> WithBehaviors<T>(this QueueOptionsBase<T> options, IEnumerable<IQueueBehavior<T>> behaviors) where T : class {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            options.Behaviors = behaviors;
            return options;
        }

        public static QueueOptionsBase<T> AddBehavior<T>(this QueueOptionsBase<T> options, IQueueBehavior<T> behavior) where T : class {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            if(behavior == null)
                throw new ArgumentNullException(nameof(behavior));
            if (options.Behaviors == null)
                options.Behaviors = new[] {behavior};
            else
                options.Behaviors = options.Behaviors.Concat(new[] {behavior});
            return options;
        }

        public static QueueOptionsBase<T> WithLoggerFactory<T>(this QueueOptionsBase<T> options, ILoggerFactory loggerFactory) where T : class {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            options.LoggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            return options;
        }

        public static QueueOptionsBase<T> WithSerializer<T>(this QueueOptionsBase<T> options, ISerializer serializer) where T : class {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            options.Serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            return options;
        }
    }
}