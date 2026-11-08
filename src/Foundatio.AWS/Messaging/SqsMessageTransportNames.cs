using System;
using System.Security.Cryptography;
using System.Text;

namespace Foundatio.Messaging.Transport;

/// <summary>
/// Maps Foundatio names to SQS queue and SNS topic names. SQS and SNS allow letters, digits, '_' and '-' (80 and 256
/// characters); Foundatio names may also contain '.' and have no length limit. A name that is already legal is used
/// as-is. Any other name, and every derived name (subscription, dead-letter and instance queues), is sanitized,
/// shortened and suffixed with a hash of the exact Foundatio name, so distinct names never map to the same queue.
/// </summary>
internal static class SqsMessageTransportNames
{
    public const int MaxNamePrefixLength = 32;
    private const int MaxQueueNameLength = 80;
    private const int MaxTopicNameLength = 256;
    private const int HashLength = 12;
    private const int InstanceTopicLength = 48;
    private const int InstanceHintLength = 14;
    private const int InstanceIdLength = 12;

    public static string Queue(string prefix, string name) =>
        Fit(prefix + name, $"queue:{name}", prefix, MaxQueueNameLength, alwaysHash: false);

    public static string Topic(string prefix, string name) =>
        Fit(prefix + name, $"topic:{name}", prefix, MaxTopicNameLength, alwaysHash: false);

    public static string Subscription(string prefix, string topic, string name) =>
        Fit($"{prefix}{topic}-{name}", $"subscription:{topic}/{name}", prefix, MaxQueueNameLength, alwaysHash: true);

    public static string DeadLetter(string prefix, MessageSource source) => source.Kind switch
    {
        MessageSourceKind.Queue => Fit($"{prefix}{source.Name}-dlq", $"deadletter:queue:{source.Name}", prefix, MaxQueueNameLength, alwaysHash: true),
        MessageSourceKind.Subscription => Fit($"{prefix}{source.Topic}-{source.Name}-dlq", $"deadletter:subscription:{source.Topic}/{source.Name}", prefix, MaxQueueNameLength, alwaysHash: true),
        MessageSourceKind.DeadLetter => DeadLetter(prefix, source.DeadLetterOf!),
        _ => throw new ArgumentException($"{source.Kind} sources do not have a dead-letter queue.", nameof(source))
    };

    /// <summary>The name every instance queue of a topic starts with, so a sweep can list them by prefix.</summary>
    public static string InstancePrefix(string prefix, string topic) =>
        Fit(prefix + topic, $"instance:{topic}", prefix, InstanceTopicLength, alwaysHash: true) + "-i-";

    public static string NewInstance(string instancePrefix, string hint)
    {
        string sanitizedHint = Sanitize(hint);
        if (sanitizedHint.Length > InstanceHintLength)
            sanitizedHint = sanitizedHint[..InstanceHintLength];

        string id = Guid.NewGuid().ToString("N")[..InstanceIdLength];
        return sanitizedHint.Length > 0 ? $"{instancePrefix}{sanitizedHint}-{id}" : instancePrefix + id;
    }

    public static void ValidatePrefix(string prefix, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(prefix, parameterName);
        if (prefix.Length > MaxNamePrefixLength || !IsLegal(prefix))
            throw new ArgumentException($"The name prefix \"{prefix}\" must be at most {MaxNamePrefixLength} letters, digits, '_' or '-'.", parameterName);
    }

    private static string Fit(string readable, string key, string prefix, int maxLength, bool alwaysHash)
    {
        if (!alwaysHash && readable.Length <= maxLength && IsLegal(readable))
            return readable;

        string sanitized = Sanitize(readable);
        int readableLength = maxLength - HashLength - 1;
        if (sanitized.Length > readableLength)
            sanitized = sanitized[..readableLength];

        return $"{sanitized}-{Hash(prefix + "|" + key)}";
    }

    private static string Hash(string value)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(value), hash);
        return Convert.ToHexString(hash[..(HashLength / 2)]).ToLowerInvariant();
    }

    private static string Sanitize(string value)
    {
        return String.Create(value.Length, value, static (span, source) =>
        {
            for (int i = 0; i < source.Length; i++)
                span[i] = IsLegal(source[i]) ? source[i] : '_';
        });
    }

    private static bool IsLegal(string value)
    {
        foreach (char c in value)
        {
            if (!IsLegal(c))
                return false;
        }

        return true;
    }

    private static bool IsLegal(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-';
}
