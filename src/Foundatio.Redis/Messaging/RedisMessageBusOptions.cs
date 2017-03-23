using System;
using StackExchange.Redis;

namespace Foundatio.Messaging {
    public class RedisMessageBusOptions : MesssageBusOptions {
        public ISubscriber Subscriber { get; set; }
    }
}