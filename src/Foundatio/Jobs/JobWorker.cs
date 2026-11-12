using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs.Store;
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
                    await RunAsync(claimed).AnyContext();
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

    private async Task RunAsync(JobInfo job)
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
        using var jobCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancelled.Token, _abortJobs.Token);
        using var lease = new LeaseRenewal(this, job, cancelled);
        using var activity = FoundatioDiagnostics.ActivitySource.StartActivity($"job {job.Name}");
        activity?.SetTag("job.id", job.Id);
        activity?.SetTag("job.name", job.Name);
        long started = Stopwatch.GetTimestamp();

        JobResult? result = null;
        Exception? error = null;
        var previous = JobRunContext.Current;
        JobRunContext.Current = context;
        try
        {
            await using var scope = _runtime.ScopeFactory.CreateAsyncScope();
            result = await registration.Invoke(scope.ServiceProvider, arguments, context, jobCancellation.Token).AnyContext();
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
            _logger.LogWarning(error, "Job {JobId} lost its lease while running; another worker may run it again", job.Id);
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

        var completion = Decide(job, policy, result, error, cancelled.IsCancellationRequested);
        await CompleteAsync(job, completion).AnyContext();

        string outcome = completion.Status == JobStatus.Queued ? (completion.RefundAttempt ? "released" : "retry") : completion.Status.ToString().ToLowerInvariant();
        var tags = new TagList { { "job.name", job.Name }, { "outcome", outcome } };
        JobsInstruments.Completed.Add(1, tags);
        JobsInstruments.Duration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds, tags);
        if (completion.Status is JobStatus.Failed or JobStatus.Queued && !completion.RefundAttempt)
            activity?.SetErrorStatus(error, completion.Error);
    }

    private JobCompletion Decide(JobInfo job, RetryPolicy policy, JobResult? result, Exception? error, bool cancellationRequested)
    {
        if (error is null && (result is null || result.IsSuccess))
        {
            _logger.LogDebug("Job {JobName} {JobId} succeeded", job.Name, job.Id);
            return JobCompletion.Succeeded(_runtime.Options.RetainSucceededFor, String.IsNullOrEmpty(result?.Message) ? null : result.Message);
        }

        if (error is OperationCanceledException || result?.IsCancelled == true)
        {
            if (cancellationRequested)
            {
                _logger.LogInformation("Job {JobName} {JobId} was cancelled", job.Name, job.Id);
                return JobCompletion.Cancelled(_runtime.Options.RetainFailedFor, result?.Message is { Length: > 0 } message ? message : null);
            }

            if (_abortJobs.IsCancellationRequested)
            {
                _logger.LogInformation("Job {JobName} {JobId} was interrupted by shutdown and will run again", job.Name, job.Id);
                return JobCompletion.Release();
            }
        }

        error ??= result?.Error ?? new JobFailedException(result?.Message is { Length: > 0 } failure ? failure : "The job reported failure.");
        string description = $"{error.GetType().Name}: {error.Message}";
        if (policy.CanRetry(error, job.Attempt))
        {
            var delay = policy.GetDelay(job.Attempt);
            _logger.LogWarning(error, "Job {JobName} {JobId} failed on attempt {Attempt} of {MaxAttempts}; retrying in {Delay}", job.Name, job.Id, job.Attempt, policy.MaxAttempts, delay);
            return JobCompletion.Retry(delay, description);
        }

        _logger.LogError(error, "Job {JobName} {JobId} failed after {Attempt} attempts", job.Name, job.Id, job.Attempt);
        return JobCompletion.Failed(_runtime.Options.RetainFailedFor, description);
    }

    private async Task CompleteAsync(JobInfo job, JobCompletion completion)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                if (!await _runtime.Store.CompleteAsync(job.Id, job.ClaimToken!, completion, CancellationToken.None).AnyContext())
                    _logger.LogWarning("Could not record the result of job {JobId}: another worker has claimed it", job.Id);
                else
                    _runtime.Observer?.OnCompleted(job, completion);

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

    /// <summary>Renews a running job's lease, and cancels it when cancellation is requested or the lease is lost.</summary>
    private sealed class LeaseRenewal : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _task;

        public LeaseRenewal(JobWorker worker, JobInfo job, CancellationTokenSource cancelled)
        {
            var lease = worker._runtime.Options.LeaseDuration;
            var interval = TimeSpan.FromTicks(Math.Min(lease.Ticks / 3, MaxRenewInterval.Ticks));
            _task = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    try
                    {
                        await worker._runtime.TimeProvider.SafeDelay(interval, _stop.Token).AnyContext();
                        if (_stop.IsCancellationRequested)
                            return;

                        var status = await worker._runtime.Store.RenewAsync(job.Id, job.ClaimToken!, lease, _stop.Token).AnyContext();
                        if (status == JobLeaseStatus.CancellationRequested && !cancelled.IsCancellationRequested)
                        {
                            worker._logger.LogInformation("Cancellation requested for job {JobId}", job.Id);
                            await cancelled.CancelAsync().AnyContext();
                        }
                        else if (status == JobLeaseStatus.Lost)
                        {
                            IsLost = true;
                            await cancelled.CancelAsync().AnyContext();
                            return;
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

        public bool IsLost { get; private set; }

        public async Task StopAsync()
        {
            _stop.Cancel();
            await _task.AnyContext();
        }

        public void Dispose()
        {
            _stop.Cancel();
            _stop.Dispose();
        }
    }
}
