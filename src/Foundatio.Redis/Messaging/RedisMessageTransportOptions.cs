using System;
using Microsoft.Extensions.Logging;

namespace Foundatio.Messaging.Transport;

public sealed record RedisMessageTransportOptions
{
    /// <summary>Prepended to every key. Each source's keys share a hash tag, so a source lives on one cluster slot.</summary>
    public string KeyPrefix { get; init; } = "foundatio:messaging";

    /// <summary>The Redis database to use, or -1 for the connection's default.</summary>
    public int Database { get; init; } = -1;

    /// <summary>
    /// How long an instance subscription survives without a heartbeat. Its receiver renews it every third of this
    /// time; once it lapses (the process stopped), publishers and new receivers on the topic delete it.
    /// </summary>
    public TimeSpan InstanceSubscriptionTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The largest message accepted, measured by <see cref="MessageSize.Of(OutgoingMessage)"/>: body bytes plus the UTF-8
    /// bytes of every header name and value, the message id, the group key and the deduplication id. Larger ones are
    /// rejected with <see cref="MessageTooLargeException"/>. Defaults to 16 MiB and can be raised to 256 MiB. Each
    /// message is a single Redis value that scripts copy while sending and receiving, so large messages block Redis
    /// and use its memory; store large payloads elsewhere and send a reference instead.
    /// </summary>
    public long MaxMessageSize { get; init; } = 16 * 1024 * 1024;

    /// <summary>
    /// The most dead letters kept in each dead-letter queue, or <c>null</c> (the default) to keep every one until it is
    /// replayed or deleted. Dead-lettering a message into a full queue removes its oldest dead letters. Dead letters
    /// are kept in Redis memory, so bound them with this or <see cref="DeadLetterRetention"/>.
    /// </summary>
    public long? DeadLetterMaxLength { get; init; }

    /// <summary>
    /// How long a dead letter is kept, or <c>null</c> (the default) to keep it until it is replayed or deleted. Older
    /// dead letters are removed whenever a message is dead-lettered into, or received from, the same dead-letter queue.
    /// At least 1 millisecond; needs Redis 6.2 or later.
    /// </summary>
    public TimeSpan? DeadLetterRetention { get; init; }

    /// <summary>How long a <see cref="OutgoingMessage.DeduplicationId"/> is remembered. At least 1 millisecond.</summary>
    public TimeSpan DeduplicationWindow { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The first wait between polls of an empty source after a receive returned messages. Waits double up to
    /// <see cref="MaxPollInterval"/> and stay there across receives until messages arrive again. At least 1 millisecond.
    /// </summary>
    public TimeSpan MinPollInterval { get; init; } = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// The longest wait between polls of an empty source; it cannot be shorter than <see cref="MinPollInterval"/>.
    /// Sending to a source, or settling in a way that makes a message available, also publishes a Redis pub/sub signal
    /// that wakes its idle receivers in every process at once, so this bounds delivery latency only when a signal is
    /// lost (for example while a connection reconnects). Each idle receiver polls about once per interval.
    /// </summary>
    public TimeSpan MaxPollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Used to turn <see cref="OutgoingMessage.DeliverAt"/> into a delay. Delays are then measured by the Redis clock.</summary>
    public TimeProvider? TimeProvider { get; init; }

    public ILoggerFactory? LoggerFactory { get; init; }
}
