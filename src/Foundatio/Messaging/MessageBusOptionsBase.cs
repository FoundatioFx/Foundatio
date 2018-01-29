using System;
using Foundatio.Serializer;
using Microsoft.Extensions.Logging;

namespace Foundatio.Messaging {
    public class SharedMessageBusOptions {
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

    public interface ISharedMessageBusOptionsBuilder : IOptionsBuilder {}

    public static class SharedMessageBusOptionsBuilderExtensions {
        public static T Topic<T>(this T builder, string topic) where T: ISharedMessageBusOptionsBuilder {
            if (string.IsNullOrEmpty(topic))
                throw new ArgumentNullException(nameof(topic));
            builder.Target<SharedMessageBusOptions>().Topic = topic;
            return (T)builder;
        }

        public static T TaskQueueMaxItems<T>(this T builder, int maxItems) where T: ISharedMessageBusOptionsBuilder {
            builder.Target<SharedMessageBusOptions>().TaskQueueMaxItems = maxItems;
            return (T)builder;
        }

        public static T TaskQueueMaxDegreeOfParallelism<T>(this T builder, byte maxDegree) where T: ISharedMessageBusOptionsBuilder {
            builder.Target<SharedMessageBusOptions>().TaskQueueMaxDegreeOfParallelism = maxDegree;
            return (T)builder;
        }

        public static T Serializer<T>(this T builder, ISerializer serializer) where T: ISharedMessageBusOptionsBuilder {
            builder.Target<SharedMessageBusOptions>().Serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            return (T)builder;
        }

        public static T LoggerFactory<T>(this T builder, ILoggerFactory loggerFactory) where T: ISharedMessageBusOptionsBuilder {
            builder.Target<SharedMessageBusOptions>().LoggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            return (T)builder;
        }
    }
}