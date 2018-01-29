using System;
using Foundatio.Serializer;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;

namespace Foundatio.Messaging {
    public class SharedMessageBusOptions : SharedOptions {
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
    }

    public interface ISharedMessageBusOptionsBuilder : ISharedOptionsBuilder {}

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
    }
}