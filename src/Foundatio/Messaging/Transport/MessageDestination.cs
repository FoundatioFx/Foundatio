namespace Foundatio.Messaging.Transport;

public enum MessageDestinationKind
{
    Queue,
    Topic
}

/// <summary>
/// Where a message is sent: a queue (one consumer handles it) or a topic (every subscription gets a copy).
/// Names contain only letters, digits, '.', '_' and '-'.
/// </summary>
public sealed record MessageDestination
{
    private MessageDestination(MessageDestinationKind kind, string name)
    {
        TransportNames.Validate(name, nameof(name));
        Kind = kind;
        Name = name;
    }

    public MessageDestinationKind Kind { get; }
    public string Name { get; }

    public static MessageDestination Queue(string name) => new(MessageDestinationKind.Queue, name);
    public static MessageDestination Topic(string name) => new(MessageDestinationKind.Topic, name);

    public override string ToString() => $"{Kind.ToString().ToLowerInvariant()}:{Name}";
}
