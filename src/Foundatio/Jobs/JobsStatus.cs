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
    private const int PageSize = 100;
    private const int MaxRead = 1000;

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
    /// Make <paramref name="within"/> longer than the time between occurrences plus the job's longest run. A schedule's
    /// succeeded jobs are kept, even with a <see cref="CronJobOptions.RetainSucceededFor"/> of zero, until its next
    /// occurrence is due or its <see cref="CronJobOptions.MisfireThreshold"/> has passed, whichever is first. Reads the newest 1,000 succeeded jobs of the schedule's job name
    /// at most, in pages of 100, so jobs of the same name enqueued in between can hide an older success.
    /// </remarks>
    /// <exception cref="ArgumentException">No CRON schedule of that name is registered in this process.</exception>
    public Task<JobDetails?> GetLastSucceededAsync(string scheduleName, TimeSpan within, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scheduleName);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(within, TimeSpan.Zero);
        return FindLastSucceededAsync(GetSchedule(scheduleName), _runtime.TimeProvider.GetUtcNow() - within, cancellationToken);
    }

    /// <summary>
    /// Whether the CRON schedule <paramref name="scheduleName"/> is overdue: no occurrence of it created in the last
    /// <paramref name="succeededWithin"/> has succeeded (see <see cref="GetLastSucceededAsync"/>), and the schedule is
    /// not new. A schedule is new, so that a fresh job store doesn't report an hourly or daily schedule as failing until
    /// its first occurrence, while none of its jobs has failed or been cancelled and it has been known for less than
    /// <paramref name="succeededWithin"/>: by this process since it set up its jobs, and by the store since the oldest of
    /// the schedule's jobs it still keeps. Foundatio.Extensions.Hosting's CRON health check fails when this is true.
    /// </summary>
    /// <remarks>
    /// While this process is new, reads the schedule's newest 1,000 jobs at most, in pages of 100. An instance that
    /// starts after the schedule's jobs have all expired, for example a daily one that keeps them for less than a day,
    /// treats the schedule as new: it agrees with the other instances once it has run for <paramref name="succeededWithin"/>.
    /// </remarks>
    /// <exception cref="ArgumentException">No CRON schedule of that name is registered in this process.</exception>
    public async Task<bool> IsScheduleOverdueAsync(string scheduleName, TimeSpan succeededWithin, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scheduleName);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(succeededWithin, TimeSpan.Zero);
        var schedule = GetSchedule(scheduleName);
        var since = _runtime.TimeProvider.GetUtcNow() - succeededWithin;
        if (await FindLastSucceededAsync(schedule, since, cancellationToken).AnyContext() is not null)
            return false;

        if (_runtime.CreatedAt <= since)
            return true;

        var query = new JobQuery { Name = schedule.JobName, Limit = PageSize };
        for (int read = 0; read < MaxRead; read += PageSize)
        {
            var page = await _runtime.Store.QueryAsync(query, cancellationToken).AnyContext();
            foreach (var job in page.Jobs)
            {
                if (job.ScheduleName != schedule.Name)
                    continue;
                if (job.CreatedAt < since || job.State is JobState.Failed or JobState.Cancelled)
                    return true;
                if (job.State is JobState.Succeeded)
                    return false;
            }

            if (page.ContinuationToken is null)
                return false;

            query = query with { ContinuationToken = page.ContinuationToken };
        }

        return false;
    }

    private CronSchedule GetSchedule(string scheduleName)
    {
        if (_runtime.Registrations.Schedules.TryGetValue(scheduleName, out var schedule))
            return schedule;

        throw new ArgumentException($"No CRON schedule named \"{scheduleName}\" is registered in this process. "
            + $"Registered: {(_runtime.Registrations.Schedules.Count > 0 ? String.Join(", ", _runtime.Registrations.Schedules.Keys) : "none")}.", nameof(scheduleName));
    }

    private async Task<JobDetails?> FindLastSucceededAsync(CronSchedule schedule, DateTimeOffset since, CancellationToken cancellationToken)
    {
        var query = new JobQuery { State = JobState.Succeeded, Name = schedule.JobName, Limit = PageSize };
        for (int read = 0; read < MaxRead; read += PageSize)
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
