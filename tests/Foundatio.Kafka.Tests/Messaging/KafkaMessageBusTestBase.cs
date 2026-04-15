using System;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Foundatio.Tests.Messaging;
using Xunit;

namespace Foundatio.Kafka.Tests.Messaging;

public class KafkaMessageBusTestBase : MessageBusTestBase
{
    protected readonly string Topic = $"test_{Guid.NewGuid().ToString("N")[..10]}";
    protected readonly string GroupId = $"group_{Guid.NewGuid().ToString("N")[..10]}";

    public KafkaMessageBusTestBase(ITestOutputHelper output) : base(output)
    {
        EnableTopicDeletion = true;
    }

    protected override IMessageBus? GetMessageBus(Func<SharedMessageBusOptions, SharedMessageBusOptions>? config = null)
    {
        return new KafkaMessageBus(o =>
        {
            o.BootstrapServers("127.0.0.1:9092");
            o.Topic(Topic);
            o.TopicReplicationFactor(1);
            o.TopicNumberOfPartitions(1);
            o.GroupId(GroupId);
            o.AllowAutoCreateTopics(true);
            //.Debug("consumer,cgrp,topic,fetch")
            o.LoggerFactory(Log);

            config?.Invoke(o.Target);

            return o;
        });
    }

    protected override async Task CleanupMessageBusAsync(IMessageBus messageBus)
    {
        if (EnableTopicDeletion && messageBus is IKafkaMessageBus kafkaMessageBus)
        {
            try
            {
                await kafkaMessageBus.DeleteTopicAsync();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        await base.CleanupMessageBusAsync(messageBus);
    }

    public bool EnableTopicDeletion { get; set; }
}
