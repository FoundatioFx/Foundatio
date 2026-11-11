using Foundatio.Jobs.Store;

namespace Foundatio.Jobs;

/// <summary>Sees how each job run ends. Used by <c>FoundatioTestHarness</c>.</summary>
internal interface IJobsObserver
{
    /// <summary>When false, CRON schedules do not create occurrences on their own; the observer triggers them.</summary>
    bool RunSchedules { get; }

    /// <summary>Called after the store recorded <paramref name="completion"/> for <paramref name="job"/>.</summary>
    void OnCompleted(JobInfo job, JobCompletion completion);
}
