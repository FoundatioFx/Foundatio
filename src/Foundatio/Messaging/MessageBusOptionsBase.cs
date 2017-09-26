using System;
using Foundatio.Serializer;
using Microsoft.Extensions.Logging;

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