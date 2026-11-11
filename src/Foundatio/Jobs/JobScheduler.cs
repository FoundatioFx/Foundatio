using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
                if (schedule.Expression.GetNextOccurrence(now, schedule.TimeZone) is { } next && next < nextWake)
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
        var occurrence = schedule.Expression
            .GetOccurrences(now - schedule.Options.MisfireThreshold, now, schedule.TimeZone, fromInclusive: true, toInclusive: true)
            .Cast<DateTimeOffset?>()
            .LastOrDefault();

        if (occurrence is not { } due || _lastCreated.TryGetValue(schedule.Name, out var last) && last >= due)
            return;

        if (!schedule.Options.AllowConcurrent && await IsPreviousOccurrenceActiveAsync(schedule, cancellationToken).AnyContext())
        {
            _lastCreated[schedule.Name] = due;
            _logger.LogInformation("Skipping {Schedule} at {Occurrence}: the previous occurrence has not finished", schedule.Name, due);
            return;
        }

        string jobId = $"cron:{schedule.Name}:{due.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}";
        await _client.CreateAsync(schedule.JobName, _payloads[schedule.Name], new EnqueueOptions { JobId = jobId, RunAt = due }, cancellationToken, schedule.Name).AnyContext();
        _lastCreated[schedule.Name] = due;
        _lastJobIds[schedule.Name] = jobId;
        _logger.LogDebug("Scheduled {Schedule} occurrence {Occurrence} as job {JobId}", schedule.Name, due, jobId);
    }

    private async Task<bool> IsPreviousOccurrenceActiveAsync(CronSchedule schedule, CancellationToken cancellationToken)
    {
        // Normally one read: the last occurrence this instance created or found. Only the first check after
        // startup has to search for an unfinished occurrence created before this instance started.
        if (_lastJobIds.TryGetValue(schedule.Name, out string? lastJobId))
            return await _runtime.Store.GetAsync(lastJobId, cancellationToken).AnyContext() is { } last && !last.Status.IsTerminal();

        foreach (var status in new[] { JobStatus.Running, JobStatus.Queued })
        {
            string? continuation = null;
            do
            {
                var page = await _runtime.Store.QueryAsync(new JobQuery { Name = schedule.JobName, Status = status, Limit = 100, ContinuationToken = continuation }, cancellationToken).AnyContext();
                if (page.Jobs.FirstOrDefault(j => j.ScheduleName == schedule.Name) is { } active)
                {
                    _lastJobIds[schedule.Name] = active.Id;
                    return true;
                }

                continuation = page.ContinuationToken;
            } while (continuation is not null);
        }

        return false;
    }
}
