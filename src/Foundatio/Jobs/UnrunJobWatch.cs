using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs.Store;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;

namespace Foundatio.Jobs;

/// <summary>
/// Warns when a job this process enqueued, and does not run itself, is still waiting a minute after it was due while no
/// job of its name is running: usually no process registers the job with <c>AddJob</c>, so its jobs wait forever. It
/// follows one job per name, read once it has been due a minute, and stops checking a name once a job of it is seen to
/// run, so an enqueue costs nothing extra and a name costs one or two store reads per process.
/// </summary>
internal sealed class UnrunJobWatch : IDisposable
{
    public static readonly TimeSpan Threshold = TimeSpan.FromMinutes(1);

    private readonly JobsRuntime _runtime;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _disposed = new();

    public UnrunJobWatch(JobsRuntime runtime, ILogger logger)
    {
        _runtime = runtime;
        _logger = logger;
    }

    /// <summary>Starts following <paramref name="job"/>, unless its name is followed already, or is known to run.</summary>
    public void OnCreated(EnqueuedJobName name, JobInfo job)
    {
        if (job.AvailableAt - _runtime.TimeProvider.GetUtcNow() > Threshold || !name.TryStartCheck())
            return;

        _ = Task.Run(() => FollowAsync(name, job.Id, job.AvailableAt), CancellationToken.None);
    }

    private async Task FollowAsync(EnqueuedJobName name, string jobId, DateTimeOffset dueAt)
    {
        var token = _disposed.Token;
        bool warned = false;
        try
        {
            await _runtime.TimeProvider.SafeDelay(dueAt + Threshold - _runtime.TimeProvider.GetUtcNow(), token).AnyContext();
            while (!token.IsCancellationRequested)
            {
                var job = await _runtime.Store.GetAsync(jobId, token).AnyContext();
                if (job is not { State: JobState.Queued, Attempt: 0 })
                {
                    // A job that ran shows a worker runs the name; one cancelled or removed before it ran shows nothing.
                    bool ran = job is { Attempt: > 0 } || job is null && warned;
                    if (ran && warned)
                        _logger.LogInformation("Job {JobName} is being run: job {JobId} has left the queue", name.Name, jobId);

                    name.EndCheck(ran);
                    return;
                }

                if (await _runtime.Store.CountAsync(JobState.Running, name.Name, token).AnyContext() > 0)
                {
                    name.EndCheck(ran: true);
                    return;
                }

                if (!warned)
                {
                    _logger.LogWarning("Job {JobName} {JobId} has been due since {DueAt} and no job of that name is running. Jobs of a name wait until a process "
                        + "that registers the job with AddJob runs them; check that the process meant to run it registers it", name.Name, jobId, dueAt);
                    warned = true;
                }

                await _runtime.TimeProvider.SafeDelay(Threshold, token).AnyContext();
            }
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Could not check whether job {JobName} {JobId} is being run", name.Name, jobId);
        }
        catch (OperationCanceledException)
        {
        }

        name.EndCheck(ran: false);
    }

    /// <summary>Stops following. The token source is not disposed, so a check that starts afterwards still reads it.</summary>
    public void Dispose() => _disposed.Cancel();
}

/// <summary>A job name this process enqueues, with whether <see cref="UnrunJobWatch"/> still needs to check that it runs.</summary>
internal sealed class EnqueuedJobName
{
    private const int Unchecked = 0;
    private const int Checking = 1;
    private const int Runs = 2;

    private int _state;

    public EnqueuedJobName(string name, bool runsHere)
    {
        Name = name;
        _state = runsHere ? Runs : Unchecked;
    }

    public string Name { get; }

    /// <summary>False once a job of the name ran, or while one is followed: reading this is all an enqueue then costs.</summary>
    public bool NeedsCheck => Volatile.Read(ref _state) == Unchecked;

    public bool TryStartCheck() => Interlocked.CompareExchange(ref _state, Checking, Unchecked) == Unchecked;

    /// <summary>Stops following; unless a job ran, the next job enqueued is followed instead.</summary>
    public void EndCheck(bool ran) => Interlocked.CompareExchange(ref _state, ran ? Runs : Unchecked, Checking);
}
