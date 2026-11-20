namespace Foundatio.Jobs;

/// <summary>Where a job is in its lifecycle.</summary>
public enum JobState
{
    /// <summary>Waiting to run, including delayed jobs and jobs waiting to retry.</summary>
    Queued,
    /// <summary>Claimed by a worker, which holds its lease while it runs.</summary>
    Running,
    /// <summary>Finished without throwing.</summary>
    Succeeded,
    /// <summary>Used up its attempts, or could not run, for example because its arguments could not be read.</summary>
    Failed,
    /// <summary>Stopped, or never started, because cancellation was requested.</summary>
    Cancelled
}
