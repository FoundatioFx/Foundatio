using System;
using System.Text;
using StackExchange.Redis;

namespace Foundatio.Messaging.Transport;

/// <summary>
/// The keys of one receivable source. Every key shares the source's hash tag, and a dead-letter queue shares the
/// tag of the source it belongs to, so each script touches a single cluster slot.
/// </summary>
internal sealed class RedisSourceKeys
{
    private readonly string _base;

    private RedisSourceKeys(string baseKey, bool orderedGroups = true, string? deadLetterBase = null)
    {
        _base = baseKey;
        OrderedGroups = orderedGroups;
        Stream = baseKey + ":stream";
        Leases = baseKey + ":leases";
        Delayed = baseKey + ":delayed";
        DelayedData = baseKey + ":delayed-data";
        Groups = baseKey + ":groups";
        Held = baseKey + ":held";
        DeadLetterStream = (deadLetterBase ?? baseKey + ":dlq") + ":stream";
    }

    /// <summary>
    /// False for dead-letter queues: they are read in batches to peek and replay, so their groups are not held back.
    /// </summary>
    public bool OrderedGroups { get; }

    public RedisKey Stream { get; }
    public RedisKey Leases { get; }
    public RedisKey Delayed { get; }
    public RedisKey DelayedData { get; }
    public RedisKey Groups { get; }
    public RedisKey Held { get; }
    public RedisKey DeadLetterStream { get; }

    public RedisKey[] All => [Stream, Leases, Delayed, DelayedData, Groups, Held];

    public RedisKey DeduplicationKey(string deduplicationId) => $"{_base}:dedup:{deduplicationId}";

    public RedisSourceKeys DeadLetter => new(_base + ":dlq", orderedGroups: false, deadLetterBase: _base + ":dlq");

    public static RedisSourceKeys Queue(string prefix, string name) => new($"{prefix}:{{q:{RedisTransportNames.Validate(name)}}}");

    public static RedisSourceKeys Subscription(string prefix, string topic, string name) =>
        new($"{prefix}:{{s:{RedisTransportNames.ValidateTopic(topic)}/{RedisTransportNames.Validate(name)}}}");

    public static RedisSourceKeys Instance(string prefix, string topic, string instance) =>
        new($"{prefix}:{{i:{RedisTransportNames.ValidateTopic(topic)}/{instance}}}");
}

/// <summary>The keys that route a topic: its durable subscriptions and its live instance subscriptions.</summary>
internal sealed class RedisTopicKeys
{
    private readonly string _base;

    public RedisTopicKeys(string prefix, string topic)
    {
        Topic = RedisTransportNames.ValidateTopic(topic);
        _base = $"{prefix}:{{t:{Topic}}}";
        Subscriptions = _base + ":subscriptions";
        Instances = _base + ":instances";
    }

    public string Topic { get; }
    public RedisKey Subscriptions { get; }
    public RedisKey Instances { get; }

    public RedisKey DeduplicationKey(string deduplicationId) => $"{_base}:dedup:{deduplicationId}";
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
