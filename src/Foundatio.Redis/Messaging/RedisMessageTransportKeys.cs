using System;
using System.Text;
using StackExchange.Redis;

namespace Foundatio.Messaging.Transport;

/// <summary>
/// The keys of one receivable source. Every key shares the source's hash tag, and a dead-letter queue shares the
/// tag of the source it belongs to, so each script touches a single cluster slot. Instance subscriptions share their
/// topic's tag, so one script can copy a publish into all of them.
/// </summary>
internal sealed class RedisSourceKeys
{
    private readonly string _base;

    private RedisSourceKeys(string description, string baseKey, bool orderedGroups = true, string? deadLetterBase = null)
    {
        _base = baseKey;
        Description = description;
        OrderedGroups = orderedGroups;
        Stream = baseKey + ":stream";
        Leases = baseKey + ":leases";
        Delayed = baseKey + ":delayed";
        DelayedData = baseKey + ":delayed-data";
        Groups = baseKey + ":groups";
        Held = baseKey + ":held";
        Retries = baseKey + ":retries";
        Idle = baseKey + ":idle";
        WakeChannelPrefix = baseKey + ":wake:";
        string deadLetter = deadLetterBase ?? baseKey + ":dlq";
        DeadLetterStream = deadLetter + ":stream";
        DeadLetterIdle = deadLetter + ":idle";
        DeadLetterWakeChannelPrefix = deadLetter + ":wake:";
    }

    /// <summary>
    /// False for dead-letter queues: they are read in batches to peek and replay, so their groups are not held back.
    /// </summary>
    public bool OrderedGroups { get; }

    /// <summary>The source as <see cref="MessageSource.ToString"/> describes it, for logging.</summary>
    public string Description { get; }

    public RedisKey Stream { get; }
    public RedisKey Leases { get; }
    public RedisKey Delayed { get; }
    public RedisKey DelayedData { get; }
    public RedisKey Groups { get; }
    public RedisKey Held { get; }

    /// <summary>The abandoned messages waiting out a retry delay, scored by when they are due, like their leases.</summary>
    public RedisKey Retries { get; }

    /// <summary>
    /// The receivers waiting for a message, each registered by a receive that found the source drained while it had
    /// room for more, scored by when. Making a message available wakes one of them.
    /// </summary>
    public RedisKey Idle { get; }

    /// <summary>Followed by a consumer name, the channel that wakes that receiver; it shares the source's hash tag.</summary>
    public string WakeChannelPrefix { get; }

    public RedisKey DeadLetterStream { get; }
    public RedisKey DeadLetterIdle { get; }
    public string DeadLetterWakeChannelPrefix { get; }

    public RedisKey[] All => [Stream, Leases, Delayed, DelayedData, Groups, Held, Retries, Idle];

    public RedisKey DeduplicationKey(string deduplicationId) => $"{_base}:dedup:{deduplicationId}";

    public RedisSourceKeys DeadLetter => new($"deadletter:{Description}", _base + ":dlq", orderedGroups: false, deadLetterBase: _base + ":dlq");

    public static RedisSourceKeys Queue(string prefix, string name) =>
        new($"queue:{name}", $"{prefix}:{{q:{RedisTransportNames.Validate(name)}}}");

    public static RedisSourceKeys Subscription(string prefix, string topic, string name) =>
        new($"subscription:{topic}/{name}", $"{prefix}:{{s:{RedisTransportNames.ValidateTopic(topic)}/{RedisTransportNames.Validate(name)}}}");

    public static RedisSourceKeys Instance(string prefix, string topic, string instance) =>
        new($"instance:{topic}/{instance}", $"{RedisTopicKeys.Base(prefix, RedisTransportNames.ValidateTopic(topic))}:i:{instance}");
}

/// <summary>The keys that route a topic: its durable subscriptions and its live instance subscriptions.</summary>
internal sealed class RedisTopicKeys
{
    private readonly string _base;

    public RedisTopicKeys(string prefix, string topic)
    {
        Topic = RedisTransportNames.ValidateTopic(topic);
        _base = Base(prefix, Topic);
        Subscriptions = _base + ":subscriptions";
        Instances = _base + ":instances";
    }

    public string Topic { get; }
    public RedisKey Subscriptions { get; }
    public RedisKey Instances { get; }

    public RedisKey DeduplicationKey(string deduplicationId) => $"{_base}:dedup:{deduplicationId}";

    public static string Base(string prefix, string topic) => $"{prefix}:{{t:{topic}}}";
}

internal static class RedisTransportNames
{
    public static string Validate(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.AsSpan().IndexOfAny('{', '}') >= 0)
            throw new ArgumentException($"\"{name}\" is not a valid Redis transport name: '{{' and '}}' are reserved for hash tags.", nameof(name));

        return name;
    }

    public static string ValidateTopic(string topic)
    {
        Validate(topic);
        if (topic.Contains('/'))
            throw new ArgumentException($"\"{topic}\" is not a valid Redis transport topic: '/' separates a topic from its subscription.", nameof(topic));

        return topic;
    }

    public static string NewInstanceName(string hint)
    {
        var builder = new StringBuilder(hint.Length + 33);
        foreach (char c in hint)
            builder.Append(Char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '-');

        return builder.Append('-').Append(Guid.NewGuid().ToString("N")).ToString();
    }
}
