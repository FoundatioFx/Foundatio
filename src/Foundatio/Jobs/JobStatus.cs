namespace Foundatio.Jobs;

public enum JobStatus
{
    /// <summary>Waiting to run, including delayed jobs and jobs waiting to retry.</summary>
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled
}

public static class JobStatusExtensions
{
    public static bool IsTerminal(this JobStatus status) => status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Cancelled;
}
