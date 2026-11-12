namespace Foundatio.Messaging;

/// <summary>Options for subscribing to published events.</summary>
public sealed record SubscriptionOptions : HandlerOptions
{
    /// <summary>
    /// Defaults to <see cref="SubscriptionScope.Service"/> for registered subscribers and
    /// <see cref="SubscriptionScope.Instance"/> for <see cref="IMessageClient.SubscribeAsync{T}"/>.
    /// </summary>
    public SubscriptionScope? Scope { get; init; }

    /// <summary>
    /// The subscription's name. For <see cref="SubscriptionScope.Service"/> it names the durable subscription and defaults
    /// to the service name; for <see cref="SubscriptionScope.Instance"/> it is a hint for the transport. Subscribers of a
    /// topic with the same name share each delivery, so give a subscriber its own name to retry it, and set its options,
    /// on its own. Renaming a durable subscription starts a new one with events published from then on, and leaves the
    /// old one's backlog behind.
    /// </summary>
    public string? Name { get; init; }
}
