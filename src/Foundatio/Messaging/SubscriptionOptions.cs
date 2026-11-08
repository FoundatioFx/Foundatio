namespace Foundatio.Messaging;

/// <summary>Options for subscribing to published events.</summary>
public sealed record SubscriptionOptions : HandlerOptions
{
    /// <summary>
    /// Defaults to <see cref="SubscriptionScope.Service"/> for registered subscribers and
    /// <see cref="SubscriptionScope.Instance"/> for <see cref="IMessageClient.SubscribeAsync{T}"/>.
    /// </summary>
    public SubscriptionScope? Scope { get; init; }

    /// <summary>The durable subscription name for service scope. Defaults to the service name.</summary>
    public string? Name { get; init; }
}
