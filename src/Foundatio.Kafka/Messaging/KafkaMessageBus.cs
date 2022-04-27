using System;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Messaging; 

public class KafkaMessageBus : MessageBusBase<KafkaMessageBusOptions>
{
    public KafkaMessageBus(KafkaMessageBusOptions options) : base(options)
    {
    }

    public KafkaMessageBus(Builder<KafkaMessageBusOptionsBuilder, KafkaMessageBusOptions> config)
        : this(config(new KafkaMessageBusOptionsBuilder()).Build())
    {
    }

    protected override Task PublishImplAsync(string messageType, object message, MessageOptions options, CancellationToken cancellationToken) {
        throw new NotImplementedException();
    }
}