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
        public static MessageBusOptionsBase WithTopic(this MessageBusOptionsBase options, string topic) {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrEmpty(topic))
                throw new ArgumentNullException(nameof(topic));
            options.Topic = topic;
            return options;
        }

        public static MessageBusOptionsBase WithTaskQueueMaxItems(this MessageBusOptionsBase options, int maxItems) {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            options.TaskQueueMaxItems = maxItems;
            return options;
        }

        public static MessageBusOptionsBase WithTaskQueueMaxDegreeOfParallelism(this MessageBusOptionsBase options, byte maxDegree) {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            options.TaskQueueMaxDegreeOfParallelism = maxDegree;
            return options;
        }

        public static MessageBusOptionsBase WithSerializer(this MessageBusOptionsBase options, ISerializer serializer) {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            options.Serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            return options;
        }

        public static MessageBusOptionsBase WithLoggerFactory(this MessageBusOptionsBase options, ILoggerFactory loggerFactory) {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            options.LoggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            return options;
        }
    }
}