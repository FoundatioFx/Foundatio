using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs.Store;
using Foundatio.Serializer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Foundatio.Jobs;

/// <summary>State shared by the job client, worker and scheduler.</summary>
internal sealed class JobsRuntime
{
    private TaskCompletionSource _workAvailable = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public JobsRuntime(IJobStore store, JobsOptions options, JobsRegistrations registrations, ISerializer serializer,
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
    }

    public IJobStore Store { get; }
    public JobsOptions Options { get; }
    public JobsRegistrations Registrations { get; }
    public IReadOnlyDictionary<string, JobRegistration> Jobs => Registrations.Jobs;
    public ISerializer Serializer { get; }
    public IServiceScopeFactory ScopeFactory { get; }
    public ILoggerFactory LoggerFactory { get; }
    public TimeProvider TimeProvider { get; }
    public string WorkerId { get; }

    /// <summary>Completes when a job is enqueued in this process, so an idle local worker starts it at once.</summary>
    public Task WorkAvailable => Volatile.Read(ref _workAvailable).Task;

    public void SignalWorkAvailable()
    {
        Interlocked.Exchange(ref _workAvailable, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    }
}
