using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Foundatio.Messaging.Transport;

/// <summary>
/// Derives exchange and queue names from a source's own fields. A subscription's topic and name are joined with ':',
/// which source names cannot contain, so no two sources share a name. A name longer than <see cref="MaxNameLength"/>
/// is cut and suffixed with a hash of its exact parts.
/// </summary>
internal sealed class RabbitMQMessageTransportNames
{
    /// <summary>
    /// The longest queue or exchange name the transport uses. A delayed message's routing key is the 28 delay bits
    /// followed by its target's name, and routing keys are limited to 255 bytes.
    /// </summary>
    public const int MaxNameLength = 255 - 2 * RabbitMQDelayLevels.Count;

    private const int HashLength = 10;
    private const int MaxPrefixLength = 64;

    private readonly string _prefix;
    private readonly string _targetPrefix;

    public RabbitMQMessageTransportNames(string prefix)
    {
        _prefix = prefix;
        _targetPrefix = $"{prefix}.";
    }

    public static void ValidatePrefix(string prefix, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix, parameterName);
        if (prefix.Length > MaxPrefixLength || prefix.Any(c => !(Char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')))
            throw new ArgumentException($"The prefix \"{prefix}\" must be at most {MaxPrefixLength} letters, digits, '.', '_' and '-'.", parameterName);
        if ($"{prefix}.".StartsWith("amq.", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"The prefix \"{prefix}\" cannot start with \"amq\": RabbitMQ reserves those names.", parameterName);
    }

    public string Queue(string name) => Build("queue", name);

    /// <summary>The fanout exchange a topic is published to.</summary>
    public string Topic(string name) => Build("topic", name);

    public string Subscription(string topic, string name) => Build("subscription", topic, name);

    /// <summary>A new, unique name for an instance subscription's queue.</summary>
    public string Instance(string topic, string hint) => Build("instance", topic, $"{hint}.{Guid.NewGuid():N}");

    public string DeadLetter(MessageSource source) => source.Kind switch
    {
        MessageSourceKind.Queue => Build("deadletter.queue", source.Name),
        MessageSourceKind.Subscription => Build("deadletter.subscription", source.Topic!, source.Name),
        MessageSourceKind.DeadLetter => DeadLetter(source.DeadLetterOf!),
        _ => throw new ArgumentException($"{source.Kind} sources do not have a dead-letter queue.", nameof(source))
    };

    /// <summary>The queue a queue, subscription or dead-letter source receives from.</summary>
    public string For(MessageSource source) => source.Kind switch
    {
        MessageSourceKind.Queue => Queue(source.Name),
        MessageSourceKind.Subscription => Subscription(source.Topic!, source.Name),
        MessageSourceKind.DeadLetter => DeadLetter(source.DeadLetterOf!),
        _ => throw new ArgumentException("Instance subscriptions get a new queue name for each receiver.", nameof(source))
    };

    /// <summary>The exchange and queue of delay level <paramref name="level"/>, which hold a message for 2^level seconds.</summary>
    public string DelayLevel(int level) => $"{_prefix}.delay.{level:D2}";

    /// <summary>
    /// The exchange delayed messages reach once their delay has passed. It routes a message to its target's landing
    /// queue, and one whose target has none to <see cref="DelayFinal"/>, its alternate exchange.
    /// </summary>
    public string DelayDelivery => $"{_prefix}.delay";

    /// <summary>The exchange every queue and topic the transport uses is bound to by its name, which delivers delayed messages to their targets.</summary>
    public string DelayFinal => $"{_prefix}.delay.final";

    /// <summary>
    /// The landing queue of <paramref name="target"/>, a queue or a topic's exchange, which takes its due delayed
    /// messages from <see cref="DelayDelivery"/> and dead-letters them into it through <see cref="DelayFinal"/>.
    /// </summary>
    public string Landing(string target) => Build("landing", target.StartsWith(_targetPrefix, StringComparison.Ordinal) ? target[_targetPrefix.Length..] : target);

    /// <summary>
    /// The target of the landing queue <paramref name="landing"/>, or null when its name is not a landing queue's or
    /// may have been shortened, which loses the target's exact name.
    /// </summary>
    public string? LandingTarget(string landing)
    {
        string landingPrefix = $"{_prefix}.landing.";
        if (!landing.StartsWith(landingPrefix, StringComparison.Ordinal) || landing.Length <= landingPrefix.Length || landing.Length >= MaxNameLength)
            return null;

        return _targetPrefix + landing[landingPrefix.Length..];
    }

    /// <summary>
    /// The exchange and queue that drop what nothing else takes: published messages without subscriptions and delayed
    /// messages whose target was deleted. Routing them somewhere lets delay queues dead-letter them and move on.
    /// </summary>
    public string Discard => $"{_prefix}.discard";

    private string Build(string kind, params string[] parts)
    {
        string readable = $"{_prefix}.{kind}.{String.Join(':', parts)}";
        if (readable.Length <= MaxNameLength)
            return readable;

        return $"{readable[..(MaxNameLength - HashLength - 1)]}.{Hash(kind, parts)}";
    }

    private static string Hash(string kind, string[] parts)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}\n{String.Join('\n', parts)}"));
        return Convert.ToHexString(hash, 0, HashLength / 2).ToLowerInvariant();
    }
}
