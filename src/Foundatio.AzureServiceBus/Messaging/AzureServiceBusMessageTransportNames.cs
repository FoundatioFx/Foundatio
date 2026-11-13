using System;
using System.Security.Cryptography;
using System.Text;

namespace Foundatio.Messaging.Transport;

/// <summary>
/// Maps Foundatio names to Service Bus entity names. Queues and topics share one namespace in Service Bus, while a
/// Foundatio queue and topic may have the same name, so queues are named <c>{prefix}queue.{name}</c> and topics
/// <c>{prefix}topic.{name}</c>. Service Bus allows letters, digits, '.', '_' and '-' (all the characters Foundatio names
/// have), up to 260 characters for queues and topics and 50 for subscriptions, starting and ending with a letter or
/// digit. A name that already fits is used as-is; any other name is shortened, trimmed and suffixed with a hash of the
/// exact Foundatio name, so distinct names never map to the same entity. Service Bus names are case-insensitive, so two
/// Foundatio names that differ only in case share an entity.
/// </summary>
internal sealed class AzureServiceBusMessageTransportNames
{
    public const int MaxPrefixLength = 32;
    public const int MaxEntityNameLength = 260;
    public const int MaxSubscriptionNameLength = 50;
    private const int HashLength = 12;
    private const int InstanceIdLength = 12;

    private readonly string _prefix;

    public AzureServiceBusMessageTransportNames(string prefix)
    {
        ValidatePrefix(prefix, nameof(prefix));
        _prefix = prefix;
    }

    public string Queue(string name) => Fit($"{_prefix}queue.{name}", $"{_prefix}|queue:{name}", MaxEntityNameLength);

    public string Topic(string name) => Fit($"{_prefix}topic.{name}", $"{_prefix}|topic:{name}", MaxEntityNameLength);

    /// <summary>Subscriptions live under their topic, so the prefix is not part of their name.</summary>
    public static string Subscription(string name) => Fit(name, $"subscription:{name}", MaxSubscriptionNameLength);

    /// <summary>A unique instance subscription name: the hint, shortened, then a random id.</summary>
    public static string NewInstance(string hint)
    {
        string id = Guid.NewGuid().ToString("N")[..InstanceIdLength];
        string readable = Trim(hint, MaxSubscriptionNameLength - InstanceIdLength - 1);
        return readable.Length > 0 ? $"{readable}-{id}" : id;
    }

    /// <summary>
    /// The Service Bus message id for a deduplication id sent to one subscription. Its topic deduplicates for all of
    /// its subscriptions, so the id is hashed with the subscription's name: the same deduplication id published to the
    /// topic, or sent to another subscription, is not a duplicate.
    /// </summary>
    public static string SubscriptionDeduplicationId(string subscription, string deduplicationId)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes($"{subscription}\n{deduplicationId}"), hash);
        return "s-" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static void ValidatePrefix(string prefix, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(prefix, parameterName);
        if (prefix.Length > MaxPrefixLength || (prefix.Length > 0 && !IsAlphanumeric(prefix[0])) || !IsLegal(prefix))
            throw new ArgumentException($"The name prefix \"{prefix}\" must be at most {MaxPrefixLength} letters, digits, '.', '_' or '-', starting with a letter or digit.", parameterName);
    }

    private static string Fit(string readable, string key, int maxLength)
    {
        if (readable.Length <= maxLength && IsLegal(readable) && IsAlphanumeric(readable[0]) && IsAlphanumeric(readable[^1]))
            return readable;

        string trimmed = Trim(readable, maxLength - HashLength - 1);
        string hash = Hash(key);
        return trimmed.Length > 0 ? $"{trimmed}-{hash}" : hash;
    }

    /// <summary>Replaces characters Service Bus does not allow, shortens to <paramref name="maxLength"/> and trims the ends to a letter or digit.</summary>
    private static string Trim(string value, int maxLength)
    {
        var builder = new StringBuilder(Math.Min(value.Length, maxLength));
        foreach (char c in value)
        {
            if (builder.Length == maxLength)
                break;

            builder.Append(IsLegal(c) ? c : '-');
        }

        int start = 0;
        while (start < builder.Length && !IsAlphanumeric(builder[start]))
            start++;

        int end = builder.Length;
        while (end > start && !IsAlphanumeric(builder[end - 1]))
            end--;

        return builder.ToString(start, end - start);
    }

    private static string Hash(string value)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(value), hash);
        return Convert.ToHexString(hash[..(HashLength / 2)]).ToLowerInvariant();
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

    private static bool IsLegal(char c) => IsAlphanumeric(c) || c is '.' or '_' or '-';

    private static bool IsAlphanumeric(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9');
}
