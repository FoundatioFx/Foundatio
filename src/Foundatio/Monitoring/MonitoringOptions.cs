using System;

namespace Foundatio.Monitoring;

/// <summary>Settings for <c>AddFoundatioMonitoring</c>: what each process tracks, and how it shares snapshots.</summary>
public sealed class MonitoringOptions
{
    /// <summary>How often each instance writes its snapshot to the cache. Defaults to 5 seconds.</summary>
    /// <remarks>
    /// An instance also writes sooner, at most about once a second, when work becomes visible, finishes visibly or
    /// reports progress. Backlog and job counts read by <see cref="IWorkMonitor"/> are cached for this long too.
    /// </remarks>
    public TimeSpan SnapshotInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a snapshot lasts in the cache, so an instance that stops without shutting down drops out. Defaults to
    /// three times <see cref="SnapshotInterval"/>; it must be longer than the interval.
    /// </summary>
    public TimeSpan? SnapshotExpiry { get; set; }

    /// <summary>
    /// How long a message handler or job runs before it is listed as active work. Work that reports progress is listed
    /// at once. Defaults to 1 second. Stats count all work, however short.
    /// </summary>
    public TimeSpan ActiveThreshold { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How many finished items each instance keeps for <see cref="IWorkMonitor.GetRecentAsync"/>: work that was listed
    /// as active, and every retry, failure, dead letter and cancellation. Defaults to 100.
    /// </summary>
    public int RecentCapacity { get; set; } = 100;

    /// <summary>How far back stats reach, in whole minutes counted in one-minute buckets. Defaults to 5 minutes.</summary>
    public TimeSpan StatsWindow { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Prefixes the cache keys snapshots are stored under. Defaults to <c>foundatio:monitoring</c>.</summary>
    public string KeyPrefix { get; set; } = "foundatio:monitoring";

    /// <summary>
    /// The service name instances show. Defaults to the messaging service name, then the host's application name.
    /// </summary>
    public string? ServiceName { get; set; }

    /// <summary>The snapshot expiry in effect: <see cref="SnapshotExpiry"/>, or three times <see cref="SnapshotInterval"/>.</summary>
    internal TimeSpan EffectiveSnapshotExpiry => SnapshotExpiry ?? SnapshotInterval * 3;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(SnapshotInterval, TimeSpan.Zero, nameof(SnapshotInterval));
        if (EffectiveSnapshotExpiry <= SnapshotInterval)
            throw new ArgumentOutOfRangeException(nameof(SnapshotExpiry), SnapshotExpiry, "SnapshotExpiry must be longer than SnapshotInterval, or snapshots expire between writes.");

        ArgumentOutOfRangeException.ThrowIfLessThan(ActiveThreshold, TimeSpan.Zero, nameof(ActiveThreshold));
        ArgumentOutOfRangeException.ThrowIfLessThan(RecentCapacity, 0, nameof(RecentCapacity));
        ArgumentOutOfRangeException.ThrowIfLessThan(StatsWindow, TimeSpan.FromMinutes(1), nameof(StatsWindow));
        ArgumentException.ThrowIfNullOrWhiteSpace(KeyPrefix, nameof(KeyPrefix));
        if (ServiceName is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(ServiceName, nameof(ServiceName));
    }
}
