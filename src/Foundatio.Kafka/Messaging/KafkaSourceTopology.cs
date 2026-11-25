using System.Collections.Generic;
using Confluent.Kafka;

namespace Foundatio.Messaging.Transport;

/// <summary>The Kafka topics and consumer group behind one <see cref="MessageSource"/>.</summary>
internal sealed record KafkaSourceTopology
{
    public required MessageSource Source { get; init; }

    /// <summary>The topics the receiver reads.</summary>
    public required IReadOnlyList<string> Topics { get; init; }

    /// <summary>The consumer group, or <c>null</c> for an instance subscription, which reads assigned partitions directly.</summary>
    public string? GroupId { get; init; }

    /// <summary>For instance subscriptions, the partitions and starting offsets to read.</summary>
    public IReadOnlyList<TopicPartitionOffset> Assignment { get; init; } = [];

    /// <summary>Where abandoned and released copies go: read by this source only.</summary>
    public string? RetryTopic { get; init; }

    public string? DeadLetterTopic { get; init; }

    public bool IsInstance => GroupId is null;
}
