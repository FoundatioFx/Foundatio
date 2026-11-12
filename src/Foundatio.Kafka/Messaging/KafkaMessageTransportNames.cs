using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Foundatio.Messaging.Transport;

/// <summary>
/// Derives Kafka topic and consumer group names from a source's own fields. Kafka rejects topics whose names only
/// differ by '.' and '_', so names never contain '_': a name that has one, or that has more than one part, or that is
/// too long, is made readable and suffixed with a hash of its exact parts.
/// </summary>
internal sealed class KafkaMessageTransportNames
{
    private const int MaxTopicLength = 249;
    private const int HashLength = 10;

    private readonly string _prefix;

    public KafkaMessageTransportNames(string prefix)
    {
        _prefix = prefix;
    }

    public string Queue(string name) => Build("queue", name);

    public string Topic(string name) => Build("topic", name);

    /// <summary>A durable subscription's own topic: retries and messages addressed to that subscription only.</summary>
    public string SubscriptionInbox(string topic, string name) => Build("subscription", topic, name);

    public string DeadLetter(MessageSource source) => source.Kind switch
    {
        MessageSourceKind.Queue => Build("deadletter.queue", source.Name),
        MessageSourceKind.Subscription => Build("deadletter.subscription", source.Topic!, source.Name),
        MessageSourceKind.DeadLetter => DeadLetter(source.DeadLetterOf!),
        _ => throw new ArgumentException($"{source.Kind} sources do not have a dead-letter queue.", nameof(source))
    };

    public string Delay(TimeSpan tier) => $"{_prefix}.delay.{KafkaDelayTiers.Label(tier)}";

    public string DelayGroup => $"{_prefix}.delay";

    public bool IsDelay(string topic) => topic.StartsWith($"{_prefix}.delay.", StringComparison.Ordinal);

    public bool IsDeadLetter(string topic) => topic.StartsWith($"{_prefix}.deadletter.", StringComparison.Ordinal);

    /// <summary>Whether a delayed record may be forwarded to <paramref name="topic"/>: a queue, topic, subscription or dead-letter topic of this prefix.</summary>
    public bool IsDelayTarget(string topic)
    {
        return topic.Length <= MaxTopicLength
            && (topic.StartsWith($"{_prefix}.queue.", StringComparison.Ordinal)
                || topic.StartsWith($"{_prefix}.topic.", StringComparison.Ordinal)
                || topic.StartsWith($"{_prefix}.subscription.", StringComparison.Ordinal)
                || topic.StartsWith($"{_prefix}.deadletter.", StringComparison.Ordinal));
    }

    private string Build(string kind, params string[] parts)
    {
        string readable = $"{_prefix}.{kind}.{String.Join('.', parts.Select(p => p.Replace('_', '-')))}";
        bool exact = parts.Length == 1 && !parts[0].Contains('_');
        if (exact && readable.Length <= MaxTopicLength)
            return readable;

        int keep = Math.Min(readable.Length, MaxTopicLength - HashLength - 1);
        return $"{readable[..keep].TrimEnd('.')}.{Hash(kind, parts)}";
    }

    private static string Hash(string kind, string[] parts)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}\n{String.Join('\n', parts)}"));
        return Convert.ToHexString(hash, 0, HashLength / 2).ToLowerInvariant();
    }
}
