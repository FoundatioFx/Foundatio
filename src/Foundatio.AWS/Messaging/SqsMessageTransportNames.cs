using System;
using System.Security.Cryptography;
using System.Text;

namespace Foundatio.Messaging.Transport;

/// <summary>
/// Maps Foundatio names to SQS queue and SNS topic names. SQS and SNS allow letters, digits, '_' and '-' (80 and 256
/// characters, including the ".fifo" suffix of FIFO queues and topics); Foundatio names may also contain '.' and have
/// no length limit. A name that is already legal is used as-is. Any other name, and every derived name (subscription,
/// dead-letter and instance queues), is sanitized, shortened and suffixed with a hash of the exact Foundatio name, so
/// distinct names never map to the same queue.
/// </summary>
internal sealed class SqsMessageTransportNames
{
    public const int MaxNamePrefixLength = 32;
    public const string FifoSuffix = ".fifo";
    private const int MaxQueueNameLength = 80;
    private const int MaxTopicNameLength = 256;
    private const int MaxFifoIdLength = 128;
    private const int HashLength = 12;
    private const int FifoIdHashLength = 32;
    private const int InstanceTopicLength = 48;
    private const int InstanceHintLength = 14;
    private const int InstanceIdLength = 12;

    private readonly string _prefix;
    private readonly string _suffix;

    public SqsMessageTransportNames(string prefix, bool fifo)
    {
        ValidatePrefix(prefix, nameof(prefix));
        _prefix = prefix;
        _suffix = fifo ? FifoSuffix : String.Empty;
    }

    public string Queue(string name) =>
        Fit(_prefix + name, $"queue:{name}", MaxQueueNameLength, alwaysHash: false);

    public string Topic(string name) =>
        Fit(_prefix + name, $"topic:{name}", MaxTopicNameLength, alwaysHash: false);

    public string Subscription(string topic, string name) =>
        Fit($"{_prefix}{topic}-{name}", $"subscription:{topic}/{name}", MaxQueueNameLength, alwaysHash: true);

    public string DeadLetter(MessageSource source) => source.Kind switch
    {
        MessageSourceKind.Queue => Fit($"{_prefix}{source.Name}-dlq", $"deadletter:queue:{source.Name}", MaxQueueNameLength, alwaysHash: true),
        MessageSourceKind.Subscription => Fit($"{_prefix}{source.Topic}-{source.Name}-dlq", $"deadletter:subscription:{source.Topic}/{source.Name}", MaxQueueNameLength, alwaysHash: true),
        MessageSourceKind.DeadLetter => DeadLetter(source.DeadLetterOf!),
        _ => throw new ArgumentException($"{source.Kind} sources do not have a dead-letter queue.", nameof(source))
    };

    /// <summary>The name every instance queue of a topic starts with, so a sweep can list them by prefix.</summary>
    public string InstancePrefix(string topic) =>
        FitWithoutSuffix(_prefix + topic, $"instance:{topic}", InstanceTopicLength, alwaysHash: true) + "-i-";

    /// <summary>A unique instance queue name; the hint is shortened to leave room for the ".fifo" suffix.</summary>
    public string NewInstance(string instancePrefix, string hint)
    {
        string sanitizedHint = Sanitize(hint);
        int hintLength = InstanceHintLength - _suffix.Length;
        if (sanitizedHint.Length > hintLength)
            sanitizedHint = sanitizedHint[..hintLength];

        string id = Guid.NewGuid().ToString("N")[..InstanceIdLength];
        return (sanitizedHint.Length > 0 ? $"{instancePrefix}{sanitizedHint}-{id}" : instancePrefix + id) + _suffix;
    }

    /// <summary>
    /// A FIFO message group or deduplication id: 1 to 128 printable ASCII characters other than space. Any other value
    /// is sanitized, shortened and suffixed with a hash of the exact value.
    /// </summary>
    public static string FifoId(string value)
    {
        if (value.Length is > 0 and <= MaxFifoIdLength && IsFifoIdLegal(value))
            return value;

        var builder = new StringBuilder(MaxFifoIdLength);
        foreach (char c in value)
        {
            if (builder.Length == MaxFifoIdLength - FifoIdHashLength - 1)
                break;

            builder.Append(c is > ' ' and <= '~' ? c : '_');
        }

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(value), hash);
        return builder.Append('-').Append(Convert.ToHexString(hash[..(FifoIdHashLength / 2)]).ToLowerInvariant()).ToString();
    }

    public static void ValidatePrefix(string prefix, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(prefix, parameterName);
        if (prefix.Length > MaxNamePrefixLength || !IsLegal(prefix))
            throw new ArgumentException($"The name prefix \"{prefix}\" must be at most {MaxNamePrefixLength} letters, digits, '_' or '-'.", parameterName);
    }

    private string Fit(string readable, string key, int maxLength, bool alwaysHash) =>
        FitWithoutSuffix(readable, key, maxLength - _suffix.Length, alwaysHash) + _suffix;

    private string FitWithoutSuffix(string readable, string key, int maxLength, bool alwaysHash)
    {
        if (!alwaysHash && readable.Length <= maxLength && IsLegal(readable))
            return readable;

        string sanitized = Sanitize(readable);
        int readableLength = maxLength - HashLength - 1;
        if (sanitized.Length > readableLength)
            sanitized = sanitized[..readableLength];

        return $"{sanitized}-{Hash(_prefix + "|" + key)}";
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

    private static bool IsFifoIdLegal(string value)
    {
        foreach (char c in value)
        {
            if (c is <= ' ' or > '~')
                return false;
        }

        return true;
    }

    private static bool IsLegal(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-';
}
