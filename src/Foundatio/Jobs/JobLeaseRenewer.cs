using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs.Store;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;

namespace Foundatio.Jobs;

/// <summary>
/// Renews the leases of one worker's running jobs with a single timer, and cancels a running job's token when
/// cancellation is requested, when the store reports that another claim holds the job, or when renewals kept failing
/// until the lease this worker last proved had nearly run out. Fencing protects the job's stored state either way;
/// cancelling stops its side effects before another worker can claim it. A job that finishes within a renewal
/// interval never touches the timer or the store.
/// </summary>
internal sealed class JobLeaseRenewer
{
    private static readonly TimeSpan MaxRenewInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TimerResolution = TimeSpan.FromMilliseconds(5);

    private readonly IJobStore _store;
    private readonly TimeSpan _leaseDuration;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly long _interval;
    private readonly long _heldFor;
    private readonly long _timerResolution;
    private readonly object _lock = new();
    private readonly HashSet<RunningJob> _jobs = [];
    private readonly ITimer _timer;
    private long _timerDueAt = Int64.MaxValue;
    private bool _aborted;

    public JobLeaseRenewer(IJobStore store, TimeSpan leaseDuration, TimeProvider timeProvider, ILogger logger)
    {
        _store = store;
        _leaseDuration = leaseDuration;
        _timeProvider = timeProvider;
        _logger = logger;

        // Renew a third of the way through the lease, or every 5 s, and treat the lease as lost a quarter of it
        // (at most 5 s) before it would run out, so the job is cancelled before another worker could claim it.
        _interval = ToTimestamp(TimeSpan.FromTicks(Math.Min(leaseDuration.Ticks / 3, MaxRenewInterval.Ticks)));
        _heldFor = ToTimestamp(leaseDuration - TimeSpan.FromTicks(Math.Min(leaseDuration.Ticks / 4, MaxRenewInterval.Ticks)));
        _timerResolution = ToTimestamp(TimerResolution);

        // Renewals must not run in the context (activity, job context) of whoever started the worker.
        if (ExecutionContext.IsFlowSuppressed())
        {
            _timer = CreateTimer();
        }
        else
        {
            using (ExecutionContext.SuppressFlow())
                _timer = CreateTimer();
        }
    }

    /// <summary>Starts renewing a claimed job's lease, which runs from no earlier than <paramref name="leaseStarted"/>, a <see cref="TimeProvider"/> timestamp.</summary>
    public RunningJob Track(JobInfo job, long leaseStarted)
    {
        var running = new RunningJob(job);
        long now = _timeProvider.GetTimestamp();
        bool lost, aborted;
        lock (_lock)
        {
            aborted = _aborted;
            running.Aborted = aborted;
            running.RenewAt = now + _interval;
            running.LostAt = leaseStarted + _heldFor;
            lost = running.LostAt <= now;
            running.IsLost = lost;
            running.Tracked = !lost;
            if (!lost)
            {
                _jobs.Add(running);
                ScheduleLocked(Math.Min(running.RenewAt, running.LostAt), now);
            }
        }

        if (lost)
            OnLeaseExpired(running);
        else if (aborted)
            running.Cancel();

        return running;
    }

    /// <summary>
    /// Stops renewing <paramref name="job"/>, cancelling and waiting for a renewal in flight. The lease can no longer
    /// be lost after this, so <see cref="RunningJob.IsLost"/> is final.
    /// </summary>
    public async Task UntrackAsync(RunningJob job)
    {
        Task? renewal;
        CancellationTokenSource? renewalCancellation;
        bool expired = false;
        lock (_lock)
        {
            if (!job.Tracked)
                return;

            job.Tracked = false;
            _jobs.Remove(job);
            if (job.LostAt <= _timeProvider.GetTimestamp())
                expired = job.IsLost = true;

            renewal = job.Renewal;
            renewalCancellation = job.RenewalCancellation;
        }

        if (expired)
            OnLeaseExpired(job);

        if (renewal is null)
            return;

        TryCancel(renewalCancellation);
        await renewal.AnyContext();
    }

    /// <summary>Cancels the job with <paramref name="jobId"/> if it is running on this worker. Returns whether it was.</summary>
    public bool RequestCancellation(string jobId)
    {
        RunningJob? found = null;
        lock (_lock)
        {
            foreach (var job in _jobs)
            {
                if (!job.CancellationRequested && String.Equals(job.Job.Id, jobId, StringComparison.Ordinal))
                {
                    job.CancellationRequested = true;
                    found = job;
                    break;
                }
            }
        }

        if (found is null)
            return false;

        _logger.LogInformation("Cancellation requested for job {JobId}", jobId);
        found.Cancel();
        return true;
    }

    /// <summary>Cancels every running job at shutdown, and any tracked from now on. They keep their leases until they end.</summary>
    public void AbortAll()
    {
        List<RunningJob> running;
        lock (_lock)
        {
            _aborted = true;
            running = new List<RunningJob>(_jobs);
            foreach (var job in running)
                job.Aborted = true;
        }

        foreach (var job in running)
            job.Cancel();
    }

    private ITimer CreateTimer() => _timeProvider.CreateTimer(static state => ((JobLeaseRenewer)state!).OnTimer(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

    private void OnTimer()
    {
        List<(RunningJob Job, CancellationTokenSource? Renewal)>? expired = null;
        lock (_lock)
        {
            _timerDueAt = Int64.MaxValue;
            long now = _timeProvider.GetTimestamp();
            long next = Int64.MaxValue;
            foreach (var job in _jobs)
            {
                if (job.IsLost)
                    continue;

                if (job.LostAt <= now)
                {
                    job.IsLost = true;
                    (expired ??= []).Add((job, job.RenewalCancellation));
                    continue;
                }

                next = Math.Min(next, job.LostAt);
                if (job.Renewal is not null)
                    continue;

                if (job.RenewAt <= now + _timerResolution)
                {
                    var cancellation = new CancellationTokenSource();
                    job.RenewalCancellation = cancellation;
                    job.Renewal = Task.Run(() => RenewAsync(job, cancellation), CancellationToken.None);
                }
                else
                {
                    next = Math.Min(next, job.RenewAt);
                }
            }

            if (next != Int64.MaxValue)
                ScheduleLocked(next, now);
        }

        if (expired is null)
            return;

        foreach (var (job, renewal) in expired)
        {
            OnLeaseExpired(job);
            TryCancel(renewal);
        }
    }

    private async Task RenewAsync(RunningJob job, CancellationTokenSource cancellation)
    {
        long sent = _timeProvider.GetTimestamp();
        JobLeaseStatus? status = null;
        Exception? error = null;
        try
        {
            status = await _store.RenewAsync(job.Job.Id, job.Job.ClaimToken!, _leaseDuration, cancellation.Token).AnyContext();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            error = ex;
        }

        bool tracked, lost = false, cancel = false;
        lock (_lock)
        {
            job.Renewal = null;
            job.RenewalCancellation = null;
            tracked = job.Tracked && !job.IsLost;
            if (tracked)
            {
                long now = _timeProvider.GetTimestamp();
                if (status == JobLeaseStatus.Lost)
                {
                    lost = job.IsLost = job.IsRevoked = true;
                }
                else
                {
                    if (status is not null)
                        job.LostAt = Math.Max(job.LostAt, sent + _heldFor);

                    if (status == JobLeaseStatus.CancellationRequested && !job.CancellationRequested)
                        cancel = job.CancellationRequested = true;

                    job.RenewAt = now + _interval;
                    ScheduleLocked(Math.Min(job.RenewAt, job.LostAt), now);
                }
            }
        }

        cancellation.Dispose();
        if (!tracked)
            return;

        if (error is not null)
            _logger.LogWarning(error, "Could not renew the lease on job {JobId}", job.Job.Id);

        if (cancel)
        {
            _logger.LogInformation("Cancellation requested for job {JobId}", job.Job.Id);
            job.Cancel();
        }

        if (lost)
            job.Cancel();
    }

    /// <summary>Cancels a job whose lease could not be renewed in time.</summary>
    private void OnLeaseExpired(RunningJob job)
    {
        _logger.LogWarning("Cancelling job {JobId}: its lease could not be renewed in time, so another worker may claim it", job.Job.Id);
        job.Cancel();
    }

    private void ScheduleLocked(long dueAt, long now)
    {
        if (dueAt >= _timerDueAt)
            return;

        _timerDueAt = dueAt;
        _timer.Change(TimeSpan.FromTicks(Math.Max(0, (long)((dueAt - now) * ((double)TimeSpan.TicksPerSecond / _timeProvider.TimestampFrequency)))), Timeout.InfiniteTimeSpan);
    }

    private long ToTimestamp(TimeSpan span) => (long)(span.Ticks * ((double)_timeProvider.TimestampFrequency / TimeSpan.TicksPerSecond));

    private static void TryCancel(CancellationTokenSource? cancellation)
    {
        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}

/// <summary>A job running on this worker, whose lease <see cref="JobLeaseRenewer"/> renews.</summary>
internal sealed class RunningJob : IDisposable
{
    private readonly CancellationTokenSource _cancellation = new();

    public RunningJob(JobInfo job)
    {
        Job = job;
    }

    public JobInfo Job { get; }

    /// <summary>The job's cancellation token: cancelled on request, when the lease is lost, and at shutdown.</summary>
    public CancellationToken Token => _cancellation.Token;

    /// <summary>Whether someone asked to cancel the job, through this process's client or the store.</summary>
    public volatile bool CancellationRequested;

    /// <summary>Whether shutdown cancelled the job.</summary>
    public volatile bool Aborted;

    /// <summary>
    /// Whether the lease was lost while the job ran: the store said another claim holds the job, or renewals kept
    /// failing until the lease this worker last proved had nearly run out. Final once the renewer stops tracking the job.
    /// </summary>
    public volatile bool IsLost;

    /// <summary>
    /// Whether the store said another claim holds the job, so the run's result can't be recorded. When only renewals
    /// failed, the store may still accept it: completions are fenced by the claim token, not the lease.
    /// </summary>
    public volatile bool IsRevoked;

    // Read and written under the renewer's lock.
    internal bool Tracked;
    internal long RenewAt;
    internal long LostAt;
    internal Task? Renewal;
    internal CancellationTokenSource? RenewalCancellation;

    /// <summary>Cancels the job's token without running its callbacks on the caller's thread.</summary>
    public void Cancel()
    {
        try
        {
            _ = _cancellation.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose() => _cancellation.Dispose();
}
