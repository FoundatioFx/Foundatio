using System;

namespace Foundatio.Monitoring;

/// <summary>One instance's rolling counts for a message type on a source, or for a job name, as a snapshot stores them.</summary>
internal sealed class WorkStatsEntry
{
    public WorkKind Kind { get; init; }
    public required string Name { get; init; }
    public string? Source { get; init; }
    public long Succeeded { get; init; }
    public long Retried { get; init; }
    public long Failed { get; init; }
    public long DeadLettered { get; init; }
    public long Cancelled { get; init; }
    public long Skipped { get; init; }
    public int InFlight { get; init; }

    /// <summary>How many runs <see cref="TotalDuration"/> and <see cref="MaxDuration"/> cover, so instances can be averaged by weight.</summary>
    public long Timed { get; init; }

    public TimeSpan TotalDuration { get; init; }
    public TimeSpan MaxDuration { get; init; }
}
