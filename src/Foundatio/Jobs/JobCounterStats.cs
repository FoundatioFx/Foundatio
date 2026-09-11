using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
namespace Foundatio.Jobs;

/// <summary>
/// Counter statistics for a queue, including totals and per-hour buckets for sparkline rendering.
/// </summary>
public sealed record JobCounterStats
{
    /// <summary>
    /// Sum of all counters across the requested time window.
    /// Keys are counter names (e.g., "processed", "failed", "dead_lettered").
    /// </summary>
    public required IReadOnlyDictionary<string, long> Totals { get; init; }

    /// <summary>
    /// Per-hour counter values ordered oldest to newest, suitable for sparkline rendering.
    /// Each bucket represents one UTC hour.
    /// </summary>
    public required IReadOnlyList<JobCounterBucket> Buckets { get; init; }
}

/// <summary>
/// Counter values for a single hour.
/// </summary>
public sealed record JobCounterBucket
{
    /// <summary>
    /// The UTC hour this bucket represents (truncated to the hour).
    /// </summary>
    public required DateTimeOffset Hour { get; init; }

    /// <summary>
    /// Counter values for this hour. Keys are counter names.
    /// </summary>
    public required IReadOnlyDictionary<string, long> Counters { get; init; }
}
