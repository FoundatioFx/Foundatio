using System;
using System.Diagnostics.CodeAnalysis;

namespace Foundatio.Messaging.Transport;

[Experimental(ProviderApi.DiagnosticId, UrlFormat = ProviderApi.UrlFormat)]
public enum MessageDestinationKind
{
    /// <summary>A queue: one receiver handles each message.</summary>
    Queue,

    /// <summary>A topic: every subscription gets a copy.</summary>
    Topic,

    /// <summary>One durable subscription of a topic, without copying to the topic's other subscriptions.</summary>
    Subscription
}

/// <summary>
/// Where a message is sent: a queue (one consumer handles it), a topic (every subscription gets a copy), or a single
/// durable subscription of a topic (for example to replay its dead letters). Names contain only
/// letters, digits, '.', '_' and '-'.
/// </summary>
[Experimental(ProviderApi.DiagnosticId, UrlFormat = ProviderApi.UrlFormat)]
public sealed record MessageDestination
{
    private MessageDestination(MessageDestinationKind kind, string name, string? subscriptionName)
    {
        TransportNames.Validate(name, nameof(name));
        if (subscriptionName is not null)
            TransportNames.Validate(subscriptionName, nameof(subscriptionName));

        Kind = kind;
        Name = name;
        SubscriptionName = subscriptionName;
    }

    public MessageDestinationKind Kind { get; }

    /// <summary>The queue name, or the topic name for topic and subscription destinations.</summary>
    public string Name { get; }

    /// <summary>The subscription name of a <see cref="MessageDestinationKind.Subscription"/> destination.</summary>
    public string? SubscriptionName { get; }

    public static MessageDestination Queue(string name) => new(MessageDestinationKind.Queue, name, null);
    public static MessageDestination Topic(string name) => new(MessageDestinationKind.Topic, name, null);

    /// <summary>
    /// One durable subscription of <paramref name="topic"/>: the same subscription that
    /// <see cref="MessageSource.Subscription"/> receives from.
    /// </summary>
    public static MessageDestination Subscription(string topic, string name) => new(MessageDestinationKind.Subscription, topic, name);

    /// <summary>The destination that delivers to <paramref name="source"/>: a queue, or a durable subscription.</summary>
    public static MessageDestination For(MessageSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.Kind switch
        {
            MessageSourceKind.Queue => Queue(source.Name),
            MessageSourceKind.Subscription => Subscription(source.Topic!, source.Name),
            _ => throw new ArgumentException($"Messages cannot be sent directly to a {source.Kind} source.", nameof(source))
        };
    }

    public override string ToString() => Kind == MessageDestinationKind.Subscription
        ? $"subscription:{Name}/{SubscriptionName}"
        : $"{Kind.ToString().ToLowerInvariant()}:{Name}";
}
