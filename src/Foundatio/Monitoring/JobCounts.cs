namespace Foundatio.Monitoring;

/// <summary>
/// How many retained jobs of one name are in each state, from the job store. A count is null when the store could not
/// be read.
/// </summary>
public sealed record JobCounts
{
    /// <summary>The job name.</summary>
    public required string Name { get; init; }

    /// <summary>Jobs waiting to run, including delayed jobs and jobs waiting to retry.</summary>
    public long? Queued { get; init; }

    /// <summary>Jobs a worker is running.</summary>
    public long? Running { get; init; }

    /// <summary>Succeeded jobs still within their retention.</summary>
    public long? Succeeded { get; init; }

    /// <summary>Failed jobs still within their retention.</summary>
    public long? Failed { get; init; }

    /// <summary>Cancelled jobs still within their retention.</summary>
    public long? Cancelled { get; init; }
}
