using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs.Store;
using Foundatio.Serializer;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;

namespace Foundatio.Jobs;

/// <summary>
/// Creates a job for each CRON occurrence. Every instance runs a scheduler; occurrences get deterministic ids, so
/// the store keeps exactly one job per occurrence no matter how many instances create it.
/// </summary>
internal sealed class JobScheduler
{
    private static readonly TimeSpan MaxSleep = TimeSpan.FromMinutes(1);

    // How many skipped or missed occurrences the overlap check reads back when it doesn't know a schedule's newest job.
    private const int MaxOccurrencesToCheck = 32;

    private readonly JobsRuntime _runtime;
    private readonly JobClient _client;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, DateTimeOffset> _lastCreated = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _lastJobIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ReadOnlyMemory<byte>> _payloads;
    private Task _loop = Task.CompletedTask;

    public JobScheduler(JobsRuntime runtime, JobClient client)
    {
        _runtime = runtime;
        _client = client;
        _logger = runtime.LoggerFactory.CreateLogger<JobScheduler>();
        _payloads = runtime.Registrations.Schedules.Values.ToDictionary(s => s.Name,
            s => s.Arguments is null ? ReadOnlyMemory<byte>.Empty : (ReadOnlyMemory<byte>)runtime.Serializer.SerializeToBytes<object>(s.Arguments), StringComparer.Ordinal);
        Status = new ProcessingStatusTracker(runtime.TimeProvider);
    }

    /// <summary>Read by health checks. A pass with any failing schedule is recorded as a failure.</summary>
    public ProcessingStatusTracker Status { get; }

    public ProcessingStatus GetStatus() => Status.GetStatus("scheduler");

    public void Start()
    {
        _logger.LogInformation("Scheduling {Schedules}", String.Join(", ", _runtime.Registrations.Schedules.Values.Select(s => $"{s.Name} ({s.Expression})")));
        Status.MarkRunning();
        _loop = Task.Run(RunAsync, CancellationToken.None);
    }

    public async Task StopAsync()
    {
        Status.MarkStopped();
        _stop.Cancel();
        await _loop.AnyContext();
    }

    /// <summary>Creates the jobs that are due at <paramref name="now"/>. Returns when the next occurrence is due.</summary>
    internal async Task<DateTimeOffset> RunDueAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var nextWake = now.Add(MaxSleep);
        bool failed = false;
        foreach (var schedule in _runtime.Registrations.Schedules.Values)
        {
            try
            {
                await CreateDueOccurrenceAsync(schedule, now, cancellationToken).AnyContext();
                if (schedule.GetNextOccurrence(now) is { } next && next < nextWake)
                    nextWake = next;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed = true;
                Status.RecordFailure(ex, $"Schedule {schedule.Name}");
                _logger.LogError(ex, "Error scheduling {Schedule}; other schedules are unaffected", schedule.Name);
            }
        }

        if (!failed)
            Status.RecordSuccess();

        return nextWake;
    }

    private async Task RunAsync()
    {
        var token = _stop.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                var now = _runtime.TimeProvider.GetUtcNow();
                var nextWake = await RunDueAsync(now, token).AnyContext();
                var sleep = nextWake - _runtime.TimeProvider.GetUtcNow();
                await _runtime.TimeProvider.SafeDelay(sleep < TimeSpan.FromMilliseconds(10) ? TimeSpan.FromMilliseconds(10) : sleep, token).AnyContext();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Status.RecordFailure(ex);
                _logger.LogError(ex, "Error in the job scheduler");
                await _runtime.TimeProvider.SafeDelay(TimeSpan.FromSeconds(5), token).AnyContext();
            }
        }
    }

    private async Task CreateDueOccurrenceAsync(CronSchedule schedule, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var occurrence = schedule.GetLatestOccurrence(now - schedule.Options.MisfireThreshold, now);
        if (occurrence is not { } due || _lastCreated.TryGetValue(schedule.Name, out var last) && last >= due)
            return;

        if (!schedule.Options.AllowConcurrent && await IsPreviousOccurrenceActiveAsync(schedule, due, cancellationToken).AnyContext())
        {
            _lastCreated[schedule.Name] = due;
            _logger.LogInformation("Skipping {Schedule} at {Occurrence}: the previous occurrence has not finished", schedule.Name, due);
            return;
        }

        string jobId = schedule.GetJobId(due);
        // However short the retention where it runs, the job outlives the time a scheduler could create the occurrence again.
        await _client.CreateAsync(schedule.JobName, _payloads[schedule.Name], new EnqueueOptions { JobId = jobId, RunAt = due }, cancellationToken, schedule.Name, schedule.GetKeepUntil(due), schedule.Arguments as IHaveJobKeys).AnyContext();
        _lastCreated[schedule.Name] = due;
        _lastJobIds[schedule.Name] = jobId;
        _logger.LogDebug("Scheduled {Schedule} occurrence {Occurrence} as job {JobId}", schedule.Name, due, jobId);
    }

    /// <summary>
    /// Whether an earlier occurrence of the schedule is still queued or running, whichever instance created it. Only
    /// the newest of a schedule's jobs can be: without <see cref="CronJobOptions.AllowConcurrent"/>, each is created
    /// only once the one before it has finished. Occurrence ids are the same on every instance, so the check reads jobs
    /// by id and never lists the job name's jobs, however many of them are queued.
    /// </summary>
    private async Task<bool> IsPreviousOccurrenceActiveAsync(CronSchedule schedule, DateTimeOffset due, CancellationToken cancellationToken)
    {
        // Normally one read: the job of the occurrence just before this one.
        var previous = schedule.GetPreviousOccurrence(due);
        if (previous is { } previousOccurrence && await _runtime.Store.GetAsync(schedule.GetJobId(previousOccurrence), cancellationToken).AnyContext() is { } previousJob)
        {
            _lastJobIds[schedule.Name] = previousJob.Id;
            return !previousJob.State.IsTerminal();
        }

        // The previous occurrence was skipped or missed. The newest job is usually the last one this instance created
        // or found running.
        JobInfo? last = null;
        if (_lastJobIds.TryGetValue(schedule.Name, out string? lastJobId))
        {
            last = await _runtime.Store.GetAsync(lastJobId, cancellationToken).AnyContext();
            if (last is not null && !last.State.IsTerminal())
                return true;
        }

        // Another instance may have created a newer one: read the jobs of the occurrences since, up to a bound.
        var ids = new List<string>(MaxOccurrencesToCheck);
        for (var occurrence = previous; occurrence is { } before && ids.Count < MaxOccurrencesToCheck && (last is null || before > last.ScheduledFor);)
        {
            occurrence = schedule.GetPreviousOccurrence(before);
            if (occurrence is { } older && (last is null || older > last.ScheduledFor))
                ids.Add(schedule.GetJobId(older));
        }

        var jobs = await Task.WhenAll(ids.Select(id => _runtime.Store.GetAsync(id, cancellationToken))).AnyContext();
        if (jobs.FirstOrDefault(j => j is not null) is { } newest)
        {
            _lastJobIds[schedule.Name] = newest.Id;
            return !newest.State.IsTerminal();
        }

        if (last is not null)
            return false;

        // Nothing is known of this schedule here (this instance just started): an occurrence from longer ago may still be
        // running. Running jobs are bounded by the workers' concurrency, unlike queued ones, so list those.
        string? continuation = null;
        do
        {
            var page = await _runtime.Store.QueryAsync(new JobQuery { Name = schedule.JobName, State = JobState.Running, Limit = 100, ContinuationToken = continuation }, cancellationToken).AnyContext();
            if (page.Jobs.FirstOrDefault(j => j.ScheduleName == schedule.Name) is { } running)
            {
                _lastJobIds[schedule.Name] = running.Id;
                return true;
            }

            continuation = page.ContinuationToken;
        } while (continuation is not null);

        return false;
    }
}
