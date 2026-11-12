namespace Foundatio.Messaging.Transport;

/// <summary>A delivery from a Kafka receiver: the partition assignment it came from and its offset.</summary>
internal sealed class KafkaIncomingMessage : IncomingMessage
{
    public required KafkaMessageReceiver Owner { get; init; }
    public required KafkaPartitionState Partition { get; init; }
    public required long Offset { get; init; }

    /// <summary>The record's <see cref="KafkaRecord.Size"/>, kept for when it is released back into the buffer.</summary>
    public int Size { get; init; }
}
