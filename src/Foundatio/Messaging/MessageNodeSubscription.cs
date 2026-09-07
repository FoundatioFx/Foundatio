using System;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Messaging;

/// <summary>A best-effort subscription for one running node, independent of durable service subscriptions.</summary>
public sealed record MessageNodeSubscriptionOptions
{
    /// <summary>Topic broadcast to the running nodes.</summary>
    public required string Topic { get; init; }
    /// <summary>Diagnostic node identity; providers may add a unique resource suffix.</summary>
    public string NodeId { get; init; } = Guid.NewGuid().ToString("N");
    /// <summary>Maximum callbacks running concurrently on this node.</summary>
    public int MaxConcurrency { get; init; } = 10;
    /// <summary>Interval for renewing managed node ownership.</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromMinutes(2);
    /// <summary>Time without a heartbeat before another node may clean up resources.</summary>
    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromMinutes(10);
    /// <summary>Maximum retained backlog on managed node resources.</summary>
    public TimeSpan MessageRetention { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Rejects invalid lifecycle and receiving settings.</summary>
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Topic);
        ArgumentException.ThrowIfNullOrWhiteSpace(NodeId);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxConcurrency, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(HeartbeatInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(StaleAfter, HeartbeatInterval);
    }
}

/// <summary>A provider-managed node subscription. Disposal removes its infrastructure.</summary>
public interface IManagedNodeSubscription : IAsyncDisposable
{
    /// <summary>Native subscription destination owned by this node.</summary>
    DestinationAddress Source { get; }
}

/// <summary>Manages node resources on brokers without natively expiring subscriptions. Does not advertise native expiration.</summary>
public interface ISupportsManagedNodeSubscriptions : IMessageTransport
{
    /// <summary>Creates a node subscription and starts renewing its managed ownership.</summary>
    Task<IManagedNodeSubscription> OpenNodeSubscriptionAsync(MessageNodeSubscriptionOptions options, CancellationToken cancellationToken = default);
}

internal sealed class NodeMessageSubscription(IMessageSubscription consumer, IManagedNodeSubscription owner) : IMessageSubscription
{
    public DestinationAddress Source => consumer.Source;
    public MessageSubscriptionStatus Status => consumer.Status;
    public long RecoveryVersion => consumer.RecoveryVersion;
    public Task WaitUntilReadyAsync(CancellationToken cancellationToken = default) => consumer.WaitUntilReadyAsync(cancellationToken);
    public async ValueTask DisposeAsync()
    {
        try { await consumer.DisposeAsync().ConfigureAwait(false); }
        finally { await owner.DisposeAsync().ConfigureAwait(false); }
    }
}
