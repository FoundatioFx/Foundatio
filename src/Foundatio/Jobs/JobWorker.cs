using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs.Store;
using Foundatio.Messaging;
using Foundatio.Resilience;
using Foundatio.Serializer;
using Foundatio.Utility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Foundatio.Jobs;

/// <summary>Claims due jobs this process can run and runs them with bounded concurrency.</summary>
internal sealed class JobWorker
{
    private static readonly TimeSpan MinPollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan MaxRenewInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AbortGracePeriod = TimeSpan.FromSeconds(5);

    private readonly JobsRuntime _runtime;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentDictionary<string, int> _runningByName = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopClaiming = new();
    private readonly CancellationTokenSource _abortJobs = new();
    private Task _loop = Task.CompletedTask;
    private int _running;

    public JobWorker(JobsRuntime runtime)
    {
        _runtime = runtime;
        _logger = runtime.LoggerFactory.CreateLogger<JobWorker>();
        _slots = new SemaphoreSlim(runtime.Options.MaxConcurrency, runtime.Options.MaxConcurrency);
        Status = new ProcessingStatusTracker(runtime.TimeProvider);
    }

    /// <summary>Read by health checks. Claim errors are recorded here; they never stop the worker.</summary>
    public ProcessingStatusTracker Status { get; }

    public int Running => Volatile.Read(ref _running);

    public ProcessingStatus GetStatus()
    {
        var running = _runtime.Jobs.Keys.ToDictionary(name => name, name => _runningByName.GetValueOrDefault(name), StringComparer.Ordinal);
        return Status.GetStatus("worker", running.Values.Sum(), running);
    }

    public void Start()
    {
        _logger.LogInformation("Job worker {WorkerId} running {Jobs} (concurrency {MaxConcurrency})", _runtime.WorkerId, String.Join(", ", _runtime.Jobs.Keys), _runtime.Options.MaxConcurrency);
        WarnAboutIgnoredJobAttributeProperties();
        Status.MarkRunning();
        _loop = Task.Run(RunClaimLoopAsync, CancellationToken.None);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Status.MarkStopped();
        _stopClaiming.Cancel();
        await _loop.AnyContext();

        if (!await WaitForRunningAsync(_runtime.Options.ShutdownTimeout, cancellationToken).AnyContext())
        {
            _logger.LogWarning("Cancelling {Count} running jobs at shutdown; they will run again without using an attempt", Volatile.Read(ref _running));
            _abortJobs.Cancel();
            await WaitForRunningAsync(AbortGracePeriod, CancellationToken.None).AnyContext();
        }
    }

    private async Task RunClaimLoopAsync()
    {
        try
        {
            await ClaimLoopAsync().AnyContext();
        }
        catch (Exception ex)
        {
            Status.MarkFaulted(ex);
            _logger.LogCritical(ex, "Job worker {WorkerId} stopped claiming jobs after an unexpected error", _runtime.WorkerId);
        }
    }

    private async Task ClaimLoopAsync()
    {
        var token = _stopClaiming.Token;
        var idle = MinPollInterval;

        while (!token.IsCancellationRequested)
        {
            try
            {
                await _slots.WaitAsync(token).AnyContext();
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var names = _runtime.Jobs.Values
                .Where(j => j.Options.MaxConcurrency is not { } limit || _runningByName.GetValueOrDefault(j.Name) < limit)
                .Select(j => j.Name)
                .ToList();

            JobInfo? job = null;
            var workAvailable = _runtime.WorkAvailable;
            long claimSent = _runtime.TimeProvider.GetTimestamp();
            try
            {
                if (names.Count > 0)
                    job = await _runtime.Store.ClaimAsync(new JobClaimRequest { Names = names, WorkerId = _runtime.WorkerId, LeaseDuration = _runtime.Options.LeaseDuration }, token).AnyContext();

                Status.RecordSuccess();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                _slots.Release();
                break;
            }
            catch (Exception ex)
            {
                Status.RecordFailure(ex);
                _logger.LogError(ex, "Error claiming jobs");
                idle = _runtime.Options.PollInterval;
            }

            if (job is null)
            {
                _slots.Release();
                await WaitForWorkAsync(workAvailable, idle, token).AnyContext();
                idle = TimeSpan.FromTicks(Math.Min(idle.Ticks * 2, _runtime.Options.PollInterval.Ticks));
                continue;
            }

            idle = MinPollInterval;
            Interlocked.Increment(ref _running);
            _runningByName.AddOrUpdate(job.Name, 1, (_, count) => count + 1);
            var claimed = job;
            _ = Task.Run(async () =>
            {
                try
                {
                    await RunAsync(claimed, claimSent).AnyContext();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected error running job {JobId}", claimed.Id);
                }
                finally
                {
                    _runningByName.AddOrUpdate(claimed.Name, 0, (_, count) => count - 1);
                    Interlocked.Decrement(ref _running);
                    _slots.Release();
                    if (_runtime.Jobs[claimed.Name].Options.MaxConcurrency is not null)
                        _runtime.SignalWorkAvailable();
                }
            }, CancellationToken.None);
        }
    }

    /// <param name="job">The claimed job.</param>
    /// <param name="leaseStarted">A <see cref="TimeProvider"/> timestamp taken before the claim was sent; the lease runs from no earlier than this.</param>
    private async Task RunAsync(JobInfo job, long leaseStarted)
    {
        var registration = _runtime.Jobs[job.Name];
        var policy = registration.Options.RetryPolicy ?? _runtime.Options.DefaultRetryPolicy;
        using var logScope = _logger.BeginScope(new Dictionary<string, object> { ["job.id"] = job.Id, ["job.name"] = job.Name });

        if (job.CancellationRequested)
        {
            await CompleteAsync(job, JobCompletion.Cancelled(_runtime.Options.RetainFailedFor, "Cancelled before it started")).AnyContext();
            return;
        }

        if (job.Attempt > policy.MaxAttempts)
        {
            await CompleteAsync(job, JobCompletion.Failed(_runtime.Options.RetainFailedFor,
                $"Gave up after {policy.MaxAttempts} attempts; the last attempt stopped without finishing (its worker crashed or lost the lease).")).AnyContext();
            return;
        }

        object? arguments = null;
        if (registration.ArgumentsType is { } argumentsType)
        {
            try
            {
                arguments = _runtime.Serializer.Deserialize(job.Payload, argumentsType) ?? throw new InvalidOperationException("The arguments deserialized to null.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not deserialize the arguments of job {JobId}", job.Id);
                await CompleteAsync(job, JobCompletion.Failed(_runtime.Options.RetainFailedFor, $"Could not deserialize the arguments: {ex.Message}")).AnyContext();
                return;
            }
        }

        var context = new JobRunContext(job, _runtime.Store, _runtime.TimeProvider, _logger);
        using var cancelled = new CancellationTokenSource();
        using var lease = new LeaseRenewal(this, job, cancelled, leaseStarted);
        using var jobCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancelled.Token, lease.LostToken, _abortJobs.Token);
        ActivityContext.TryParse(job.Headers.GetValueOrDefault(KnownHeaders.TraceParent), job.Headers.GetValueOrDefault(KnownHeaders.TraceState), out var parent);
        using var activity = FoundatioDiagnostics.ActivitySource.StartActivity($"job {job.Name}", ActivityKind.Consumer, parent);
        activity?.SetTag("job.id", job.Id);
        activity?.SetTag("job.name", job.Name);
        long started = Stopwatch.GetTimestamp();

        Exception? error = null;
        var previous = JobRunContext.Current;
        JobRunContext.Current = context;
        try
        {
            await using var scope = _runtime.ScopeFactory.CreateAsyncScope();
            await registration.Invoke(scope.ServiceProvider, arguments, context, jobCancellation.Token).AnyContext();
        }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            JobRunContext.Current = previous;
        }

        await lease.StopAsync().AnyContext();
        if (lease.IsLost)
        {
            _logger.LogWarning(error, "Job {JobId} lost its lease while running; its result is not recorded and another worker may run it again", job.Id);
            RecordRun(job, "lost", started);
            return;
        }

        try
        {
            await context.FlushAsync(CancellationToken.None).AnyContext();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save the progress of job {JobId}", job.Id);
        }

        JobCompletion completion;
        if (error is null)
        {
            _logger.LogDebug("Job {JobName} {JobId} succeeded", job.Name, job.Id);
            completion = JobCompletion.Succeeded(_runtime.Options.RetainSucceededFor);
        }
        else
        {
            completion = Decide(job, policy, error, cancelled.IsCancellationRequested);
        }

        await CompleteAsync(job, completion, context.Message).AnyContext();

        RecordRun(job, completion.State == JobState.Queued ? (completion.RefundAttempt ? "released" : "retry") : completion.State.ToString().ToLowerInvariant(), started);
        if (completion.State is JobState.Failed or JobState.Queued && !completion.RefundAttempt)
            activity?.SetErrorStatus(error, completion.Error);
    }

    private static void RecordRun(JobInfo job, string outcome, long started)
    {
        var tags = new TagList { { "job.name", job.Name }, { "outcome", outcome } };
        JobsInstruments.Completed.Add(1, tags);
        JobsInstruments.Duration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds, tags);
    }

    /// <summary>Legacy <see cref="JobAttribute"/> settings for hosted jobs have no effect on durable jobs; say so instead of ignoring them silently.</summary>
    private void WarnAboutIgnoredJobAttributeProperties()
    {
        foreach (var jobType in _runtime.Jobs.Values.Select(r => JobNaming.GetNamedType(r.JobType)).Distinct())
        {
            if (jobType.GetCustomAttribute<JobAttribute>() is not { } attribute)
                continue;

            var ignored = new List<string>();
            if (!attribute.IsContinuous)
                ignored.Add(nameof(JobAttribute.IsContinuous));
            if (attribute.Interval is not null)
                ignored.Add(nameof(JobAttribute.Interval));
            if (attribute.InitialDelay is not null)
                ignored.Add(nameof(JobAttribute.InitialDelay));
            if (attribute.IterationLimit != -1)
                ignored.Add(nameof(JobAttribute.IterationLimit));
            if (attribute.InstanceCount != 1)
                ignored.Add(nameof(JobAttribute.InstanceCount));

            if (ignored.Count > 0)
            {
                _logger.LogWarning("[Job] sets {Properties} on {JobType}, which durable jobs ignore: a durable job runs once each time it is enqueued or its AddCronJob schedule fires, "
                    + "and JobTypeOptions.MaxConcurrency limits how many run at once", String.Join(", ", ignored), jobType.Name);
            }
        }
    }

    /// <summary>Decides how a run that threw <paramref name="error"/> ends: cancelled, released at shutdown, retried or failed.</summary>
    private JobCompletion Decide(JobInfo job, RetryPolicy policy, Exception error, bool cancellationRequested)
    {
        if (error is OperationCanceledException)
        {
            if (cancellationRequested)
            {
                _logger.LogInformation("Job {JobName} {JobId} was cancelled", job.Name, job.Id);
                return JobCompletion.Cancelled(_runtime.Options.RetainFailedFor);
            }

            if (_abortJobs.IsCancellationRequested)
            {
                _logger.LogInformation("Job {JobName} {JobId} was interrupted by shutdown and will run again", job.Name, job.Id);
                return JobCompletion.Release();
            }
        }

        string description = $"{error.GetType().Name}: {error.Message}";
        try
        {
            if (policy.CanRetry(error, job.Attempt))
            {
                var delay = policy.GetDelay(job.Attempt);
                _logger.LogWarning(error, "Job {JobName} {JobId} failed on attempt {Attempt} of {MaxAttempts}; retrying in {Delay}", job.Name, job.Id, job.Attempt, policy.MaxAttempts, delay);
                return JobCompletion.Retry(delay, description);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The retry policy of job {JobName} threw; failing job {JobId}", job.Name, job.Id);
            return JobCompletion.Failed(_runtime.Options.RetainFailedFor, $"The retry policy threw {ex.GetType().Name}: {ex.Message} while handling {description}");
        }

        _logger.LogError(error, "Job {JobName} {JobId} failed after {Attempt} attempts", job.Name, job.Id, job.Attempt);
        return JobCompletion.Failed(_runtime.Options.RetainFailedFor, description);
    }

    /// <param name="job">The claimed job.</param>
    /// <param name="completion">How the run ended.</param>
    /// <param name="message">The run's last progress message, which the store holds as the job's message unless <paramref name="completion"/> sets one.</param>
    private async Task CompleteAsync(JobInfo job, JobCompletion completion, string? message = null)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                if (!await _runtime.Store.CompleteAsync(job.Id, job.ClaimToken!, completion, CancellationToken.None).AnyContext())
                    _logger.LogWarning("Could not record the result of job {JobId}: another worker has claimed it", job.Id);
                else
                    _runtime.Observer?.OnCompleted(message is null ? job : job with { Message = message }, completion);

                return;
            }
            catch (Exception ex) when (attempt < 3)
            {
                _logger.LogWarning(ex, "Error recording the result of job {JobId}; retrying", job.Id);
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt)).AnyContext();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not record the result of job {JobId}; it will run again when its lease expires", job.Id);
                return;
            }
        }
    }

    private async Task WaitForWorkAsync(Task workAvailable, TimeSpan delay, CancellationToken cancellationToken)
    {
        using var delaySource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await Task.WhenAny(workAvailable, Task.Delay(delay, _runtime.TimeProvider, delaySource.Token)).AnyContext();
        delaySource.Cancel();
    }

    private async Task<bool> WaitForRunningAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (Volatile.Read(ref _running) > 0)
        {
            if (Stopwatch.GetTimestamp() >= deadline || cancellationToken.IsCancellationRequested)
                return false;

            try
            {
                await Task.Delay(25, cancellationToken).AnyContext();
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Renews a running job's lease and cancels the job when cancellation is requested or the lease is lost: the store
    /// reports that another claim holds the job, or renewals kept failing until the lease this worker last proved had
    /// nearly run out. Fencing protects the job's stored state either way; cancelling stops its side effects before
    /// another worker can claim it.
    /// </summary>
    private sealed class LeaseRenewal : IDisposable
    {
        private readonly JobWorker _worker;
        private readonly JobInfo _job;
        private readonly TimeSpan _heldFor;
        private readonly CancellationTokenSource _stop = new();
        private readonly CancellationTokenSource _lost;
        private readonly CancellationTokenRegistration _expired;
        private readonly Task _task;
        private volatile bool _lostByStore;

        /// <param name="worker">The worker running the job.</param>
        /// <param name="job">The claimed job.</param>
        /// <param name="cancelled">Cancelled when someone requests cancellation of the job.</param>
        /// <param name="leaseStarted">A <see cref="TimeProvider"/> timestamp from before the claim was sent.</param>
        public LeaseRenewal(JobWorker worker, JobInfo job, CancellationTokenSource cancelled, long leaseStarted)
        {
            _worker = worker;
            _job = job;
            var timeProvider = worker._runtime.TimeProvider;
            var lease = worker._runtime.Options.LeaseDuration;
            var interval = TimeSpan.FromTicks(Math.Min(lease.Ticks / 3, MaxRenewInterval.Ticks));
            _heldFor = lease - TimeSpan.FromTicks(Math.Min(lease.Ticks / 4, MaxRenewInterval.Ticks));
            _lost = new CancellationTokenSource(Remaining(leaseStarted), timeProvider);
            _expired = _lost.Token.Register(() =>
            {
                if (!_lostByStore)
                    _worker._logger.LogWarning("Cancelling job {JobId}: its lease could not be renewed in time, so another worker may claim it", _job.Id);
            });

            _task = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested && !_lost.IsCancellationRequested)
                {
                    try
                    {
                        await timeProvider.SafeDelay(interval, _stop.Token).AnyContext();
                        if (_stop.IsCancellationRequested || _lost.IsCancellationRequested)
                            return;

                        long sent = timeProvider.GetTimestamp();
                        var status = await worker._runtime.Store.RenewAsync(job.Id, job.ClaimToken!, lease, _stop.Token).AnyContext();
                        if (status == JobLeaseStatus.Lost)
                        {
                            _lostByStore = true;
                            await _lost.CancelAsync().AnyContext();
                            return;
                        }

                        _lost.CancelAfter(Remaining(sent));
                        if (status == JobLeaseStatus.CancellationRequested && !cancelled.IsCancellationRequested)
                        {
                            worker._logger.LogInformation("Cancellation requested for job {JobId}", job.Id);
                            await cancelled.CancelAsync().AnyContext();
                        }
                    }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        worker._logger.LogWarning(ex, "Could not renew the lease on job {JobId}", job.Id);
                    }
                }
            });
        }

        /// <summary>Cancelled when the lease is lost.</summary>
        public CancellationToken LostToken => _lost.Token;

        /// <summary>Whether the lease was lost before <see cref="StopAsync"/>. A lost run's result must not be recorded.</summary>
        public bool IsLost => _lost.IsCancellationRequested;

        /// <summary>Stops renewing. The lease can no longer be lost after this, so <see cref="IsLost"/> is final.</summary>
        public async Task StopAsync()
        {
            _stop.Cancel();
            await _task.AnyContext();
            _lost.CancelAfter(Timeout.InfiniteTimeSpan);
        }

        public void Dispose()
        {
            _stop.Cancel();
            _expired.Dispose();
            _stop.Dispose();
            _lost.Dispose();
        }

        /// <summary>How much longer a lease that was renewed (or claimed) at <paramref name="sent"/> is certainly held.</summary>
        private TimeSpan Remaining(long sent)
        {
            var remaining = _heldFor - _worker._runtime.TimeProvider.GetElapsedTime(sent);
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }
}
