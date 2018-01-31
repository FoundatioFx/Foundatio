using System;
using StackExchange.Redis;

namespace Foundatio.Messaging {
    public class RedisMessageBusOptions : SharedMessageBusOptions {
        public ISubscriber Subscriber { get; set; }
    }

    public class RedisMessageBusOptionsBuilder : SharedMessageBusOptionsBuilder<RedisMessageBusOptions, RedisMessageBusOptionsBuilder> {
        public RedisMessageBusOptionsBuilder Subscriber(ISubscriber subscriber) {
            Target.Subscriber = subscriber;
            return this;
        }
    }
}