using System;
using System.Collections.Generic;

namespace Foundatio.Utility;

/// <summary>A snapshot of one background loop, for health checks and diagnostics.</summary>
public sealed record ProcessingStatus
{
    private static readonly IReadOnlyDictionary<string, int> EmptyCounts = new Dictionary<string, int>();

    /// <summary>
    /// The source a message processor receives from (for example <c>queue:orders</c> or
    /// <c>subscription:orders/billing</c>), <c>worker</c> for the job worker, or <c>scheduler</c> for the CRON scheduler.
    /// </summary>
    public required string Name { get; init; }

    public required ProcessingState State { get; init; }

    /// <summary>Messages being handled, or jobs running, right now.</summary>
    public int Active { get; init; }

    /// <summary>For the job worker, the running jobs of every registered job name.</summary>
    public IReadOnlyDictionary<string, int> ActiveByName { get; init; } = EmptyCounts;

    /// <summary>
    /// When the current run of consecutive receive, claim or scheduling errors began. Null when the last attempt
    /// succeeded. Errors are retried with backoff, so a short run of failures is usually transient.
    /// </summary>
    public DateTimeOffset? FailingSince { get; init; }

    /// <summary>The most recent error, kept after recovery for diagnostics.</summary>
    public string? LastError { get; init; }

    public DateTimeOffset? LastErrorAt { get; init; }
}
