namespace Foundatio.Messaging;

/// <summary>Which running copies of an app receive a subscriber's events.</summary>
public enum SubscriptionScope
{
    /// <summary>One copy per service. Replicas of the service compete for it, and copies wait while the service is down.</summary>
    Service,

    /// <summary>One copy per running instance, only while it runs. Use for local caches and pushing to connected clients.</summary>
    /// <remarks>
    /// Each instance opens a subscription per topic: a handler class of several event types, each on its own topic,
    /// opens one per type, and on brokers such as Redis each has its own stream and heartbeat. To use one, route the
    /// types to one topic with <c>[Message(Topic = "...")]</c> or <c>MapMessage&lt;T&gt;(name, topic: "...")</c> in every service.
    /// </remarks>
    Instance
}
