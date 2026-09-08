using System;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Jobs;

/// <summary>Observation helpers for runtime jobs and tracked broker work.</summary>
public static class JobMonitorExtensions
{
    /// <summary>Waits for terminal state. Cancelling observation does not cancel the job.</summary>
    public static async Task<JobState> WaitForCompletionAsync(this IJobMonitor monitor, string jobId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        while (true)
        {
            var state = await monitor.GetAsync(jobId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Job '{jobId}' was not found or has expired.");
            if (state.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled or JobStatus.DeadLettered)
                return state;
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }
    }
}
