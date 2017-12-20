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
}