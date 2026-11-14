using System;
using Confluent.Kafka;

namespace Foundatio.Messaging.Transport;

internal static class KafkaConsumerShutdown
{
    private static readonly TimeSpan LeavePollTimeout = TimeSpan.FromMilliseconds(100);
    private const int LeavePolls = 5;

    /// <summary>
    /// Leaves the consumer group before <see cref="IConsumer{TKey,TValue}.Close"/>. With the classic protocol, a member
    /// that closes while the group starts another cooperative rebalance can terminate without sending LeaveGroup; the
    /// broker then waits a full session timeout for it, holding up the other members' joins, commits and closes.
    /// Unsubscribing while still polling leaves the group properly, and commits the stored offsets on the way out.
    /// </summary>
    public static void LeaveGroup<TKey, TValue>(IConsumer<TKey, TValue> consumer)
    {
        try
        {
            consumer.Unsubscribe();
            for (int i = 0; i < LeavePolls; i++)
                consumer.Consume(LeavePollTimeout);
        }
        catch (KafkaException)
        {
            // Close still leaves the group, just not as reliably.
        }
    }
}
