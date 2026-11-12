namespace Foundatio.Jobs;

/// <summary>Where a job is in its lifecycle.</summary>
public enum JobState
{
    /// <summary>Waiting to run, including delayed jobs and jobs waiting to retry.</summary>
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled
}
