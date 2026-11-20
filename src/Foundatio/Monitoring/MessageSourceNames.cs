using System;
using Foundatio.Messaging.Transport;

namespace Foundatio.Monitoring;

/// <summary>Reads back the <see cref="MessageSource.ToString"/> form snapshots store sources in.</summary>
internal static class MessageSourceNames
{
    /// <summary>Parses <c>queue:name</c>, <c>subscription:topic/name</c> or <c>instance:topic/name</c>; null for anything else.</summary>
    public static MessageSource? TryParse(string? value)
    {
        if (String.IsNullOrEmpty(value) || value.IndexOf(':') is not (> 0 and var colon))
            return null;

        string kind = value[..colon];
        string rest = value[(colon + 1)..];
        int slash = rest.IndexOf('/');
        try
        {
            return kind switch
            {
                "queue" when slash < 0 => MessageSource.Queue(rest),
                "subscription" when slash > 0 => MessageSource.Subscription(rest[..slash], rest[(slash + 1)..]),
                "instance" when slash > 0 => MessageSource.InstanceSubscription(rest[..slash], rest[(slash + 1)..]),
                _ => null
            };
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
