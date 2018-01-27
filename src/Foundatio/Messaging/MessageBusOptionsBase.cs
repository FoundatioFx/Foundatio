using System;
using Foundatio.Serializer;
using Microsoft.Extensions.Logging;

namespace Foundatio.Messaging {
    public abstract class MessageBusOptionsBase {
        /// <summary>
        /// The topic name
        /// </summary>
        public string Topic { get; set; } = "messages";

        /// <summary>
        /// Controls the maximum number of backplane messages that need to be queued and sent to subscribers.
        /// </summary>
        public int TaskQueueMaxItems { get; set; } = 10000;
        /// <summary>
        /// Controls the maximum number of threads that will process queued subscriber messages.
        /// </summary>
        public byte TaskQueueMaxDegreeOfParallelism { get; set; } = 4;
        public ISerializer Serializer { get; set; }
        public ILoggerFactory LoggerFactory { get; set; }
    }

    public static class MessageBusOptionsExtensions {
        public static IOptionsBuilder<MessageBusOptionsBase> Topic(this IOptionsBuilder<MessageBusOptionsBase> builder, string topic) {
            if (builder == null)
                throw new ArgumentNullException(nameof(builder));
            if (string.IsNullOrEmpty(topic))
                throw new ArgumentNullException(nameof(topic));
            builder.Target.Topic = topic;
            return builder;
        }

        public static IOptionsBuilder<MessageBusOptionsBase> TaskQueueMaxItems(this IOptionsBuilder<MessageBusOptionsBase> builder, int maxItems) {
            if (builder == null)
                throw new ArgumentNullException(nameof(builder));
            builder.Target.TaskQueueMaxItems = maxItems;
            return builder;
        }

        public static IOptionsBuilder<MessageBusOptionsBase> TaskQueueMaxDegreeOfParallelism(this IOptionsBuilder<MessageBusOptionsBase> builder, byte maxDegree) {
            if (builder == null)
                throw new ArgumentNullException(nameof(builder));
            builder.Target.TaskQueueMaxDegreeOfParallelism = maxDegree;
            return builder;
        }

        public static IOptionsBuilder<MessageBusOptionsBase> Serializer(this IOptionsBuilder<MessageBusOptionsBase> builder, ISerializer serializer) {
            if (builder == null)
                throw new ArgumentNullException(nameof(builder));
            builder.Target.Serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            return builder;
        }

        public static IOptionsBuilder<MessageBusOptionsBase> LoggerFactory(this IOptionsBuilder<MessageBusOptionsBase> builder, ILoggerFactory loggerFactory) {
            if (builder == null)
                throw new ArgumentNullException(nameof(builder));
            builder.Target.LoggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            return builder;
        }
    }
}