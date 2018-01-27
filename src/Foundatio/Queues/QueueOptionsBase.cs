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
        public static IOptionsBuilder<QueueOptionsBase<T>> Name<T>(this IOptionsBuilder<QueueOptionsBase<T>> builder, string name) where T : class {
            if (builder == null)
                throw new ArgumentNullException(nameof(builder));
            if (string.IsNullOrEmpty(name))
                throw new ArgumentNullException(nameof(name));
            builder.Target.Name = name;
            return builder;
        }

        public static IOptionsBuilder<QueueOptionsBase<T>> Retries<T>(this IOptionsBuilder<QueueOptionsBase<T>> builder, int retries) where T : class {
            if (builder == null)
                throw new ArgumentNullException(nameof(builder));
            builder.Target.Retries = retries;
            return builder;
        }

        public static IOptionsBuilder<QueueOptionsBase<T>> WorkItemTimeout<T>(this IOptionsBuilder<QueueOptionsBase<T>> builder, TimeSpan timeout) where T : class {
            if (builder == null)
                throw new ArgumentNullException(nameof(builder));
            builder.Target.WorkItemTimeout = timeout;
            return builder;
        }

        public static IOptionsBuilder<QueueOptionsBase<T>> Behaviors<T>(this IOptionsBuilder<QueueOptionsBase<T>> builder, IEnumerable<IQueueBehavior<T>> behaviors) where T : class {
            if (builder == null)
                throw new ArgumentNullException(nameof(builder));
            builder.Target.Behaviors = behaviors;
            return builder;
        }

        public static IOptionsBuilder<QueueOptionsBase<T>> AddBehavior<T>(this IOptionsBuilder<QueueOptionsBase<T>> builder, IQueueBehavior<T> behavior) where T : class {
            if (builder == null)
                throw new ArgumentNullException(nameof(builder));
            if(behavior == null)
                throw new ArgumentNullException(nameof(behavior));
            if (builder.Target.Behaviors == null)
                builder.Target.Behaviors = new[] {behavior};
            else
                builder.Target.Behaviors = builder.Target.Behaviors.Concat(new[] {behavior});
            return builder;
        }

        public static IOptionsBuilder<QueueOptionsBase<T>> LoggerFactory<T>(this IOptionsBuilder<QueueOptionsBase<T>> builder, ILoggerFactory loggerFactory) where T : class {
            if (builder == null)
                throw new ArgumentNullException(nameof(builder));
            builder.Target.LoggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            return builder;
        }

        public static IOptionsBuilder<QueueOptionsBase<T>> Serializer<T>(this IOptionsBuilder<QueueOptionsBase<T>> builder, ISerializer serializer) where T : class {
            if (builder == null)
                throw new ArgumentNullException(nameof(builder));
            builder.Target.Serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            return builder;
        }
    }
}