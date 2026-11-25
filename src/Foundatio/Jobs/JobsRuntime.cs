using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs.Store;
using Foundatio.Monitoring;
using Foundatio.Serializer;
using Foundatio.Utility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Foundatio.Jobs;

/// <summary>State shared by the job client, worker and scheduler.</summary>
internal sealed class JobsRuntime
{
    private TaskCompletionSource _workAvailable = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _workAwaited;
    private volatile ProcessingState _hostState;
    private volatile JobWorker? _worker;
    private volatile JobScheduler? _scheduler;

    public JobsRuntime(IJobStore store, JobWorkerOptions options, JobsRegistrations registrations, ISerializer serializer,
        IServiceScopeFactory scopeFactory, ILoggerFactory loggerFactory, TimeProvider timeProvider)
    {
        Store = store;
        Options = options;
        Registrations = registrations;
        Serializer = serializer;
        ScopeFactory = scopeFactory;
        LoggerFactory = loggerFactory;
        TimeProvider = timeProvider;
        WorkerId = options.WorkerId ?? $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid().ToString("N")[..6]}";
        CreatedAt = timeProvider.GetUtcNow();
    }

    /// <summary>When this process set up its jobs, usually as the host started: its CRON schedules are known to it from then.</summary>
    public DateTimeOffset CreatedAt { get; }

    public IJobStore Store { get; }
    public JobWorkerOptions Options { get; }
    public JobsRegistrations Registrations { get; }
    public IReadOnlyDictionary<string, JobRegistration> Jobs => Registrations.Jobs;
    public ISerializer Serializer { get; }
    public IServiceScopeFactory ScopeFactory { get; }
    public ILoggerFactory LoggerFactory { get; }
    public TimeProvider TimeProvider { get; }
    public string WorkerId { get; }

    /// <summary>Notified of finished runs; set by the test harness.</summary>
    public IJobsObserver? Observer { get; init; }

    /// <summary>Follows every job run for monitoring.</summary>
    public required WorkTracker Tracker { get; init; }

    /// <summary>False under the test harness, which triggers CRON jobs explicitly.</summary>
    public bool RunSchedules => Observer?.RunSchedules ?? true;

    /// <summary>Whether the hosted service is waiting for the startup gate, has started, or has stopped.</summary>
    public ProcessingState HostState
    {
        get => _hostState;
        set => _hostState = value;
    }

    /// <summary>The running worker, once the hosted service has started it.</summary>
    public JobWorker? Worker
    {
        get => _worker;
        set => _worker = value;
    }

    /// <summary>The running scheduler, once the hosted service has started it.</summary>
    public JobScheduler? Scheduler
    {
        get => _scheduler;
        set => _scheduler = value;
    }

    /// <summary>Completes when a job is enqueued in this process, so an idle local worker starts it at once.</summary>
    /// <summary>
    /// Completes when a job is enqueued here after it was read. Reading it marks it awaited, after reading it, so a
    /// signal that skips it came before the reader's next claim, which then sees the job.
    /// </summary>
    public Task WorkAvailable
    {
        get
        {
            var task = Volatile.Read(ref _workAvailable).Task;
            Interlocked.Exchange(ref _workAwaited, 1);
            return task;
        }
    }

    /// <summary>Wakes the readers of <see cref="WorkAvailable"/>; with none since the last signal, there is nothing to replace.</summary>
    public void SignalWorkAvailable()
    {
        if (Interlocked.Exchange(ref _workAwaited, 0) == 0)
            return;

        Interlocked.Exchange(ref _workAvailable, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    }
}
