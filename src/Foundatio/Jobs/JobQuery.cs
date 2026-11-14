using System;

namespace Foundatio.Jobs;

/// <summary>Filters for listing jobs, newest first.</summary>
public sealed record JobQuery
{
    /// <summary>Only jobs in this state, or all states when null.</summary>
    public JobState? State { get; init; }
    /// <summary>Only jobs with this job name, or every name when null.</summary>
    public string? Name { get; init; }

    /// <summary>Between 1 and 1000.</summary>
    public int Limit { get; init; } = 50;

    /// <summary>From the previous page's <see cref="JobPage{TJob}.ContinuationToken"/>.</summary>
    public string? ContinuationToken { get; init; }

    /// <summary>Throws when the query is out of range. Stores call this before querying.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(Limit, 1, nameof(Limit));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Limit, 1000, nameof(Limit));
    }
}
