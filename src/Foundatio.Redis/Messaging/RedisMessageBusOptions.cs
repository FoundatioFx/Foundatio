using System;
using StackExchange.Redis;

namespace Foundatio.Messaging {
    public class RedisMessageBusOptions : MessageBusOptionsBase {
        public ISubscriber Subscriber { get; set; }
    }
}