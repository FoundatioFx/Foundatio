using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;

namespace Foundatio.Jobs;

/// <summary>
/// What this process's job worker and CRON scheduler are doing. Registered by <c>AddFoundatioJobs</c>;
/// Foundatio.Extensions.Hosting turns it into a health check.
/// </summary>
public sealed class JobsStatus
{
    private const string StartupFailed = "Startup failed; jobs will not run";
    private const int SucceededPageSize = 100;
    private const int MaxSucceededRead = 1000;

    private readonly JobsRuntime _runtime;

    internal JobsStatus(JobsRuntime runtime)
    {
        _runtime = runtime;
    }

    /// <summary>
    /// The <c>worker</c> when job types are registered, and the <c>scheduler</c> when CRON jobs are. Empty when this
    /// process only enqueues. Both are <see cref="ProcessingState.Starting"/> until the startup gate opens.
    /// </summary>
    public IReadOnlyList<ProcessingStatus> GetComponents()
    {
        var result = new List<ProcessingStatus>(2);
        var hostState = _runtime.HostState;

        if (_runtime.Jobs.Count > 0)
        {
            var idle = _runtime.Jobs.Keys.ToDictionary(name => name, _ => 0, StringComparer.Ordinal);
            result.Add(_runtime.Worker?.GetStatus() ?? NotStarted("worker", hostState) with { ActiveByName = idle });
        }

        if (_runtime.Registrations.Schedules.Count > 0 && _runtime.RunSchedules)
            result.Add(_runtime.Scheduler?.GetStatus() ?? NotStarted("scheduler", hostState));

        return result;
    }

    /// <summary>
    /// Returns the newest job of the CRON schedule <paramref name="scheduleName"/> that was created in the last
    /// <paramref name="within"/> and has succeeded, whichever instance ran it, or null when none has. Read from the job
    /// store, so every instance gives the same answer. Foundatio.Extensions.Hosting turns it into a health check.
    /// </summary>
    /// <remarks>
    /// A schedule's succeeded jobs are kept at least until its next occurrence is due, even with a
    /// <see cref="CronJobOptions.RetainSucceededFor"/> of zero, so make <paramref name="within"/> longer than the time
    /// between occurrences plus the job's longest run. Reads the newest 1,000 succeeded jobs of the schedule's job name
    /// at most, in pages of 100, so jobs of the same name enqueued in between can hide an older success.
    /// </remarks>
    /// <exception cref="ArgumentException">No CRON schedule of that name is registered in this process.</exception>
    public async Task<JobDetails?> GetLastSucceededAsync(string scheduleName, TimeSpan within, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scheduleName);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(within, TimeSpan.Zero);
        if (!_runtime.Registrations.Schedules.TryGetValue(scheduleName, out var schedule))
        {
            throw new ArgumentException($"No CRON schedule named \"{scheduleName}\" is registered in this process. "
                + $"Registered: {(_runtime.Registrations.Schedules.Count > 0 ? String.Join(", ", _runtime.Registrations.Schedules.Keys) : "none")}.", nameof(scheduleName));
        }

        var since = _runtime.TimeProvider.GetUtcNow() - within;
        var query = new JobQuery { State = JobState.Succeeded, Name = schedule.JobName, Limit = SucceededPageSize };
        for (int read = 0; read < MaxSucceededRead; read += SucceededPageSize)
        {
            var page = await _runtime.Store.QueryAsync(query, cancellationToken).AnyContext();
            foreach (var job in page.Jobs)
            {
                if (job.CreatedAt < since)
                    return null;
                if (job.ScheduleName == schedule.Name)
                    return new JobDetails(job, _runtime.Serializer);
            }

            if (page.ContinuationToken is null)
                return null;

            query = query with { ContinuationToken = page.ContinuationToken };
        }

        return null;
    }

    private static ProcessingStatus NotStarted(string name, ProcessingState hostState) => new()
    {
        Name = name,
        State = hostState == ProcessingState.Running ? ProcessingState.Starting : hostState,
        LastError = hostState == ProcessingState.Faulted ? StartupFailed : null
    };
}
