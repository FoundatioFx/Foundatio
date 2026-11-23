using System;
using System.Security.Cryptography;
using System.Text;

namespace Foundatio.Messaging.Transport;

/// <summary>
/// Maps sources to Storage queue names, which are 3 to 63 lowercase letters, digits and single hyphens. Each name is
/// the prefix, with a hyphen after it, then a readable form of the source's names, shortened to fit, followed by a hash
/// of the exact names and the kind of source, so names that differ only in case or punctuation, or past the length
/// limit, get their own queues, and a queue and a topic of the same name stay apart. A subscription's queue starts with
/// its topic's name and hash, so a publish finds the topic's subscriptions with one listing; the topic and the
/// subscription share the room left by the prefix and hashes, and a short topic leaves the rest to the subscription. A
/// dead-letter queue is its source's queue name plus <c>-poison</c>.
/// </summary>
internal sealed class AzureStorageQueueMessageTransportNames
{
    public const int MaxPrefixLength = 16;
    private const int MaxQueueNameLength = 63;
    private const int HashLength = 8;
    private const string DeadLetterSuffix = "-poison";

    private readonly string _prefix;
    private readonly int _maxTopicLength;

    public AzureStorageQueueMessageTransportNames(string prefix)
    {
        _prefix = prefix.Length == 0 || prefix.EndsWith('-') ? prefix : prefix + "-";

        // A subscription's queue is the prefix, the topic and its hash, the subscription and its hash, and the
        // dead-letter suffix; the topic gets at most half the readable room, since a topic prefix cannot depend on its subscriptions.
        int readableRoom = MaxQueueNameLength - DeadLetterSuffix.Length - _prefix.Length - 2 * (HashLength + 1) - 1;
        _maxTopicLength = readableRoom / 2 + HashLength + 1;
    }

    /// <summary>The queue of a queue source.</summary>
    public string Queue(string name) => Build(name, MaxQueueNameLength - _prefix.Length - DeadLetterSuffix.Length, "queue", name);

    /// <summary>What the names of a topic's subscription and instance queues start with.</summary>
    public string TopicPrefix(string topic) => Topic(topic) + "-";

    /// <summary>The queue of a durable subscription.</summary>
    public string Subscription(string topic, string name)
    {
        string topicPrefix = TopicPrefix(topic);
        return topicPrefix + Readable(name, MaxQueueNameLength - topicPrefix.Length - HashLength - 1 - DeadLetterSuffix.Length) + "-" + Hash("subscription", topic, name);
    }

    /// <summary>A new queue for one instance subscription receiver.</summary>
    public string Instance(string topic, string nameHint)
    {
        string topicPrefix = TopicPrefix(topic);
        string unique = Convert.ToHexString(RandomNumberGenerator.GetBytes(HashLength / 2)).ToLowerInvariant();
        return topicPrefix + Readable(nameHint, MaxQueueNameLength - topicPrefix.Length - HashLength - 1 - DeadLetterSuffix.Length) + "-" + unique;
    }

    /// <summary>The queue of a source's own messages: a queue or a durable subscription.</summary>
    public string Source(MessageSource source) => source.Kind switch
    {
        MessageSourceKind.Queue => Queue(source.Name),
        MessageSourceKind.Subscription => Subscription(source.Topic!, source.Name),
        MessageSourceKind.DeadLetter => DeadLetter(source.DeadLetterOf!),
        _ => throw new ArgumentException($"{source.Kind} sources have no fixed queue name.", nameof(source))
    };

    /// <summary>The dead-letter queue of a queue or durable subscription.</summary>
    public string DeadLetter(MessageSource source) => Source(source.Kind == MessageSourceKind.DeadLetter ? source.DeadLetterOf! : source) + DeadLetterSuffix;

    /// <summary>Lowercase letters, digits and single hyphens, starting with a letter or digit, at most <see cref="MaxPrefixLength"/> characters.</summary>
    public static void ValidatePrefix(string prefix, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(prefix, parameterName);
        if (prefix.Length == 0)
            return;

        bool valid = prefix.Length <= MaxPrefixLength && IsLowerAlphanumeric(prefix[0]) && !prefix.Contains("--", StringComparison.Ordinal);
        foreach (char c in prefix)
            valid &= IsLowerAlphanumeric(c) || c == '-';

        if (!valid)
            throw new ArgumentException($"The prefix \"{prefix}\" is not valid. Use at most {MaxPrefixLength} lowercase letters, digits and single hyphens, starting with a letter or digit.", parameterName);
    }

    private string Topic(string topic) => Build(topic, _maxTopicLength, "topic", topic);

    private string Build(string name, int maxReadableLength, string kind, params string[] parts) =>
        _prefix + Readable(name, maxReadableLength - HashLength - 1) + "-" + Hash(kind, parts);

    /// <summary>The name in lowercase, with every run of other characters than letters and digits as one hyphen, shortened to <paramref name="maxLength"/>.</summary>
    private static string Readable(string name, int maxLength)
    {
        var builder = new StringBuilder(Math.Min(name.Length, maxLength));
        foreach (char c in name)
        {
            if (builder.Length >= maxLength)
                break;

            char lower = Char.ToLowerInvariant(c);
            if (IsLowerAlphanumeric(lower))
                builder.Append(lower);
            else if (builder.Length > 0 && builder[^1] != '-')
                builder.Append('-');
        }

        while (builder.Length > 0 && builder[^1] == '-')
            builder.Length--;

        return builder.Length > 0 ? builder.ToString() : "x";
    }

    private static string Hash(string kind, params string[] parts)
    {
        var text = new StringBuilder(kind);
        foreach (string part in parts)
            text.Append('\0').Append(part);

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()), hash);
        return Convert.ToHexString(hash[..(HashLength / 2)]).ToLowerInvariant();
    }

    private static bool IsLowerAlphanumeric(char c) => c is (>= 'a' and <= 'z') or (>= '0' and <= '9');
}
