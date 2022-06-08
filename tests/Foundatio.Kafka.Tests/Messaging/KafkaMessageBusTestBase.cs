using System;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Foundatio.Tests.Messaging;
using Foundatio.Utility;
using Xunit.Abstractions;

namespace Foundatio.Kafka.Tests.Messaging;

public class KafkaMessageBusTestBase : MessageBusTestBase {
    protected readonly string _topic = $"test_{SystemClock.UtcNow.Ticks}";

    public KafkaMessageBusTestBase(ITestOutputHelper output) : base(output)
    {
    }

    protected override IMessageBus GetMessageBus(Func<SharedMessageBusOptions, SharedMessageBusOptions> config = null) {
        return new KafkaMessageBus(o => o
            .BootstrapServers("127.0.0.1:9092")
            .Topic(_topic)
            .TopicReplicationFactor(1)
            .TopicNumberOfPartitions(1)
            .GroupId("tests")
            .EnableAutoCommit(false)
            .EnableAutoOffsetStore(false)
            .AllowAutoCreateTopics(true)
            .LoggerFactory(Log)
        );
    }

    protected override async Task CleanupMessageBusAsync(IMessageBus messageBus) {
        await base.CleanupMessageBusAsync(messageBus);

        if (messageBus is IKafkaMessageBus kafkaMessageBus)
            await kafkaMessageBus.DeleteTopicAsync();
    }
}