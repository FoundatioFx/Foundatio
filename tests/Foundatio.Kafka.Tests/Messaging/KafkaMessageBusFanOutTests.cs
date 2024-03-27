using System;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Kafka.Tests.Messaging;

public class KafkaMessageBusFanOutTests : KafkaMessageBusTestBase
{

    public KafkaMessageBusFanOutTests(ITestOutputHelper output) : base(output) { }

    protected override IMessageBus GetMessageBus(Func<SharedMessageBusOptions, SharedMessageBusOptions> config = null)
    {
        return new KafkaMessageBus(o => o
            .BootstrapServers("127.0.0.1:9092")
            .Topic(Topic)
            .TopicReplicationFactor(1)
            .TopicNumberOfPartitions(1)
            .GroupId(Guid.NewGuid().ToString("N"))
            .EnableAutoCommit(false)
            .EnableAutoOffsetStore(false)
            .AllowAutoCreateTopics(true)
            .LoggerFactory(Log)
        );
    }

    [Fact(Skip = "https://github.com/confluentinc/confluent-kafka-dotnet/issues/1832")]
    public override Task CanReceiveFromMultipleSubscribersAsync()
    {
        return base.CanReceiveFromMultipleSubscribersAsync();
    }

    [Fact(Skip = "https://github.com/confluentinc/confluent-kafka-dotnet/issues/1832")]
    public override Task CanReceiveMessagesConcurrentlyAsync()
    {
        return base.CanReceiveMessagesConcurrentlyAsync();
    }
}
