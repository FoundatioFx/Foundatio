using System;
using System.Text;

namespace Foundatio.Messaging.Transport;

/// <summary>
/// Delays as a chain of fixed-TTL queues, one per power of two seconds. A delayed message's routing key spells its
/// delay in binary, highest bit first, followed by its target's name. Each level's exchange routes the message to the
/// level's queue when its bit is set, or straight on to the next level's exchange when it is not; each level's queue
/// holds a message for 2^level seconds and then dead-letters it, keeping its routing key, to the next level's exchange.
/// After level 0 the message reaches the delivery exchange, where every queue and topic the transport uses is bound by
/// its name. Every message in one level's queue waits the same time, so they expire in order at the head of the queue.
/// </summary>
internal static class RabbitMQDelayLevels
{
    /// <summary>The number of levels: delays up to 2^28 - 1 seconds, about 8.5 years.</summary>
    public const int Count = 28;

    /// <summary>The longest delay the levels can hold.</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds((1L << Count) - 1);

    /// <summary>Delays are whole seconds, rounded up so a message is never delivered early.</summary>
    public static readonly TimeSpan Resolution = TimeSpan.FromSeconds(1);

    /// <summary>The delay, in whole seconds, that delivers no earlier than <paramref name="delay"/> from now.</summary>
    public static long ToSeconds(TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero)
            return 0;

        long seconds = (long)Math.Ceiling(delay.TotalSeconds);
        return Math.Min(seconds, (long)MaxDelay.TotalSeconds);
    }

    /// <summary>How long level <paramref name="level"/>'s queue holds a message, in milliseconds.</summary>
    public static long TimeToLiveMilliseconds(int level) => (1L << level) * 1000;

    /// <summary>The routing key of a message delayed by <paramref name="seconds"/> on its way to <paramref name="target"/>.</summary>
    public static string RoutingKey(long seconds, string target)
    {
        var builder = new StringBuilder(2 * Count + target.Length);
        for (int level = Count - 1; level >= 0; level--)
            builder.Append(((seconds >> level) & 1) == 1 ? '1' : '0').Append('.');

        return builder.Append(target).ToString();
    }

    /// <summary>The binding key on level <paramref name="level"/>'s exchange that matches messages whose bit for it is <paramref name="set"/>.</summary>
    public static string LevelBindingKey(int level, bool set)
    {
        var builder = new StringBuilder(2 * Count + 2);
        for (int i = Count - 1; i > level; i--)
            builder.Append("*.");

        return builder.Append(set ? '1' : '0').Append(".#").ToString();
    }

    /// <summary>
    /// The binding key on the delivery exchange for <paramref name="target"/>: any delay bits followed by exactly its
    /// name, so a name that ends with another one's words does not match it.
    /// </summary>
    public static string DeliveryBindingKey(string target)
    {
        var builder = new StringBuilder(2 * Count + target.Length);
        for (int i = 0; i < Count; i++)
            builder.Append("*.");

        return builder.Append(target).ToString();
    }
}
