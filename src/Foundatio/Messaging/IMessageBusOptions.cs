using System;
using Foundatio.Logging;
using Foundatio.Serializer;

namespace Foundatio.Messaging {
    public interface IMessageBusOptions { }
    public class MesssageBusOptions : IMessageBusOptions {
        public string Topic { get; set; } = "messages";
        public ISerializer Serializer { get; set; }
        public ILoggerFactory LoggerFactory { get; set; }
    }
}