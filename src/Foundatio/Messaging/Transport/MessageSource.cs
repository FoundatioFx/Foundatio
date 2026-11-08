using System;

namespace Foundatio.Messaging.Transport;

public enum MessageSourceKind
{
    /// <summary>Competing receivers share the messages sent to a queue.</summary>
    Queue,

    /// <summary>A named, durable subscription to a topic. It keeps receiving copies while no receiver is open.</summary>
    Subscription,

    /// <summary>
    /// A subscription that exists only while its receiver is open. Every open instance subscription to a topic gets a
    /// copy of each message. The transport chooses a unique name and must remove it when the receiver is disposed
    /// or its process stops.
    /// </summary>
    InstanceSubscription,

    /// <summary>The dead-letter queue of another source.</summary>
    DeadLetter
}

/// <summary>A place messages are received from. Names contain only letters, digits, '.', '_' and '-'.</summary>
public sealed record MessageSource
{
    private MessageSource(MessageSourceKind kind, string name, string? topic, MessageSource? deadLetterOf)
    {
        Kind = kind;
        Name = name;
        Topic = topic;
        DeadLetterOf = deadLetterOf;
    }

    public MessageSourceKind Kind { get; }

    /// <summary>The queue name, the subscription name, or a descriptive hint for instance subscriptions.</summary>
    public string Name { get; }

    /// <summary>The topic of a subscription.</summary>
    public string? Topic { get; }

    /// <summary>For <see cref="MessageSourceKind.DeadLetter"/>, the source whose dead letters are received.</summary>
    public MessageSource? DeadLetterOf { get; }

    public static MessageSource Queue(string name)
    {
        TransportNames.Validate(name, nameof(name));
        return new MessageSource(MessageSourceKind.Queue, name, null, null);
    }

    public static MessageSource Subscription(string topic, string name)
    {
        TransportNames.Validate(topic, nameof(topic));
        TransportNames.Validate(name, nameof(name));
        return new MessageSource(MessageSourceKind.Subscription, name, topic, null);
    }

    public static MessageSource InstanceSubscription(string topic, string? nameHint = null)
    {
        TransportNames.Validate(topic, nameof(topic));
        string hint = String.IsNullOrWhiteSpace(nameHint) ? "" : TransportNames.Normalize(nameHint);
        return new MessageSource(MessageSourceKind.InstanceSubscription, hint.Length > 0 ? hint : "instance", topic, null);
    }

    public static MessageSource DeadLetter(MessageSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Kind is MessageSourceKind.DeadLetter or MessageSourceKind.InstanceSubscription)
            throw new ArgumentException($"{source.Kind} sources do not have a dead-letter queue.", nameof(source));

        return new MessageSource(MessageSourceKind.DeadLetter, source.Name, source.Topic, source);
    }

    public override string ToString() => Kind switch
    {
        MessageSourceKind.Queue => $"queue:{Name}",
        MessageSourceKind.Subscription => $"subscription:{Topic}/{Name}",
        MessageSourceKind.InstanceSubscription => $"instance:{Topic}/{Name}",
        _ => $"deadletter:{DeadLetterOf}"
    };
}
