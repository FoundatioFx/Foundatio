using System;
using Foundatio.Logging;
using Foundatio.Serializer;

namespace Foundatio.Messaging {
    public abstract class MessageBusOptionsBase {
        /// <summary>
        /// The topic name
        /// </summary>
        public string Topic { get; set; } = "messages";
        public ISerializer Serializer { get; set; }
        public ILoggerFactory LoggerFactory { get; set; }
    }
}