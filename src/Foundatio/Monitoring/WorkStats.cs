using System;

namespace Foundatio.Monitoring;

/// <summary>
/// Rolling counts over <see cref="Window"/> for one message type on one source, or one job name, summed across
/// instances. They cover all work, including handlers too fast to be listed as active.
/// </summary>
public sealed record WorkStats
{
    /// <summary>Whether these count messages or jobs.</summary>
    public WorkKind Kind { get; init; }

    /// <summary>The message type name or job name.</summary>
    public required string Name { get; init; }

    /// <summary>Where the messages were received from, for example <c>queue:orders</c>. Null for jobs.</summary>
    public string? Source { get; init; }

    /// <summary>Messages completed or jobs succeeded.</summary>
    public long Succeeded { get; init; }

    /// <summary>Attempts that failed, or asked to be retried, and will run again.</summary>
    public long Retried { get; init; }

    /// <summary>Jobs that failed for good, and messages whose outcome could not be saved. See <see cref="WorkOutcome.Failed"/>.</summary>
    public long Failed { get; init; }

    /// <summary>Messages moved to the dead-letter queue. Always 0 for jobs.</summary>
    public long DeadLettered { get; init; }

    /// <summary>Work cancelled, or interrupted by shutdown or a lost lease.</summary>
    public long Cancelled { get; init; }

    /// <summary>Messages completed without a handler. Always 0 for jobs.</summary>
    public long Skipped { get; init; }

    /// <summary>How many are being handled now, across instances.</summary>
    public int InFlight { get; init; }

    /// <summary>How long handlers ran on average, weighted by how many runs each instance timed; null when none ran in the window.</summary>
    public TimeSpan? AverageDuration { get; init; }

    /// <summary>The longest run in the window on any instance; null when none ran in it.</summary>
    public TimeSpan? MaxDuration { get; init; }

    /// <summary>How far back the counts reach.</summary>
    public TimeSpan Window { get; init; }
}
