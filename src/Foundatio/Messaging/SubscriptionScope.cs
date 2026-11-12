namespace Foundatio.Messaging;

/// <summary>Which running copies of an app receive a subscriber's events.</summary>
public enum SubscriptionScope
{
    /// <summary>One copy per service. Replicas of the service compete for it, and copies wait while the service is down.</summary>
    Service,

    /// <summary>One copy per running instance, only while it runs. Use for local caches and pushing to connected clients.</summary>
    Instance
}
