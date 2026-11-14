using System.Threading;

namespace Foundatio.Messaging.Transport;

/// <summary>A delivery from a RabbitMQ receiver: the channel and consumer it arrived on and its delivery tag.</summary>
internal sealed class RabbitMQIncomingMessage : IncomingMessage
{
    private int _settled;

    public required RabbitMQMessageReceiver Owner { get; init; }

    /// <summary>The receiver's consumer the broker delivered it to, whose prefetch it uses until it is settled.</summary>
    public required RabbitMQMessageReceiver.Consumer Consumer { get; init; }

    /// <summary>Valid only on the channel it was delivered on, until that channel closes.</summary>
    public required ulong DeliveryTag { get; init; }

    public bool IsSettled => Volatile.Read(ref _settled) == 1;

    /// <summary>Claims the delivery for one settlement; false when it was already settled or released.</summary>
    public bool TrySettle() => Interlocked.Exchange(ref _settled, 1) == 0;
}
