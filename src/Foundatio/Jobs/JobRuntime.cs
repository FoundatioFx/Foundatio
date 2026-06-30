using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace Foundatio.Jobs;

/// <summary>
/// Core-owned durable-job instruments, shared by every <see cref="JobWorker"/> so job throughput and run latency are
/// observable independent of the runtime store implementation.
/// </summary>
internal static class JobInstruments
{
    public static readonly Counter<long> Started = FoundatioDiagnostics.Meter.CreateCounter<long>("foundatio.jobs.started", description: "Number of durable jobs started");
    public static readonly Counter<long> Completed = FoundatioDiagnostics.Meter.CreateCounter<long>("foundatio.jobs.completed", description: "Number of durable jobs completed successfully");
    public static readonly Counter<long> Failed = FoundatioDiagnostics.Meter.CreateCounter<long>("foundatio.jobs.failed", description: "Number of durable jobs that failed");
    public static readonly Counter<long> Cancelled = FoundatioDiagnostics.Meter.CreateCounter<long>("foundatio.jobs.cancelled", description: "Number of durable jobs that were cancelled");
    public static readonly Histogram<double> RunTime = FoundatioDiagnostics.Meter.CreateHistogram<double>("foundatio.jobs.runtime", unit: "ms", description: "Durable job execution time");
}

public enum JobStatus
{
    Queued,
    Scheduled,
    Processing,
    Completed,
    Failed,
    Cancelled,
    DeadLettered
}

public enum ScheduledDispatchKind
{
    QueueMessage,
    PubSubMessage,
    JobOccurrence
}

public sealed record JobState
{
    public required string JobId { get; init; }
    public required string Name { get; init; }
    public string? JobType { get; init; }
    public JobStatus Status { get; init; } = JobStatus.Queued;
    public int? Progress { get; init; }
    public string? ProgressMessage { get; init; }
    public int Attempt { get; init; }
    public string? NodeId { get; init; }
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastUpdatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedUtc { get; init; }
    public DateTimeOffset? CompletedUtc { get; init; }
    public DateTimeOffset? LeaseExpiresUtc { get; init; }
    public string? Error { get; init; }
    public bool CancellationRequested { get; init; }
    public DateTimeOffset? ScheduledForUtc { get; init; }
}

public sealed record JobStatePatch
{
    public JobStatus? Status { get; init; }
    public string? JobType { get; init; }
    public int? Progress { get; init; }
    public string? ProgressMessage { get; init; }
    public string? Error { get; init; }
    public int AttemptDelta { get; init; }
    public string? NodeId { get; init; }
    public bool ClearNodeId { get; init; }
    public DateTimeOffset? LeaseExpiresUtc { get; init; }
    public bool ClearLeaseExpiresUtc { get; init; }
    public DateTimeOffset? LastUpdatedUtc { get; init; }
    public DateTimeOffset? StartedUtc { get; init; }
    public DateTimeOffset? CompletedUtc { get; init; }
    public bool? CancellationRequested { get; init; }
}

public sealed record JobQuery
{
    public string? Name { get; init; }
    public JobStatus? Status { get; init; }
    public int Limit { get; init; } = 100;

    /// <summary>
    /// When true, CRON occurrences (jobs with <see cref="JobState.ScheduledForUtc"/> set) are excluded. The job
    /// scheduler is the sole executor of occurrences, so the generic worker must not claim them — otherwise it would
    /// run them without the per-definition retry/dead-letter accounting that lives in the scheduler.
    /// </summary>
    public bool ExcludeOccurrences { get; init; }
}

public sealed record ScheduledDispatchState
{
    public required string DispatchId { get; init; }
    public ScheduledDispatchKind Kind { get; init; }
    public required string Destination { get; init; }
    public required ReadOnlyMemory<byte> Body { get; init; }
    public MessageHeaders Headers { get; init; } = MessageHeaders.Empty;
    public TransportSendOptions Options { get; init; } = new();
    public DateTimeOffset DueUtc { get; init; }
    public string? ClaimOwner { get; init; }
    public DateTimeOffset? ClaimExpiresUtc { get; init; }
    public int Attempts { get; init; }
    public string? JobId { get; init; }
}

public sealed record JobRequestOptions
{
    public string? JobId { get; init; }
    public string? Name { get; init; }
}

public sealed record JobTypeRegistration(string Name, Type JobType);

public interface IJobTypeRegistry
{
    string GetName(Type jobType);
    Type Resolve(string name);
}

public sealed class JobTypeRegistry : IJobTypeRegistry
{
    private readonly Dictionary<string, Type> _nameToType;
    private readonly Dictionary<Type, string> _typeToName;

    public JobTypeRegistry(IEnumerable<JobTypeRegistration>? registrations = null)
    {
        _nameToType = new Dictionary<string, Type>(StringComparer.Ordinal);
        _typeToName = new Dictionary<Type, string>();

        foreach (var registration in registrations ?? [])
            Add(registration);
    }

    public string GetName(Type jobType)
    {
        ArgumentNullException.ThrowIfNull(jobType);
        if (!typeof(IJob).IsAssignableFrom(jobType))
            throw new ArgumentException("Job type must implement IJob.", nameof(jobType));

        return _typeToName.TryGetValue(jobType, out string? name)
            ? name
            : jobType.FullName ?? jobType.Name;
    }

    public Type Resolve(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (_nameToType.TryGetValue(name, out var registered))
            return registered;

        var jobType = Type.GetType(name, throwOnError: false);
        if (jobType is null)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                jobType = assembly.GetType(name, throwOnError: false);
                if (jobType is not null)
                    break;
            }
        }

        if (jobType is null || !typeof(IJob).IsAssignableFrom(jobType))
            throw new InvalidOperationException($"Job type \"{name}\" could not be resolved to an IJob implementation.");

        return jobType;
    }

    private void Add(JobTypeRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentException.ThrowIfNullOrEmpty(registration.Name);
        ArgumentNullException.ThrowIfNull(registration.JobType);

        if (!typeof(IJob).IsAssignableFrom(registration.JobType))
            throw new ArgumentException("Job type must implement IJob.", nameof(registration));

        if (_nameToType.TryGetValue(registration.Name, out var existing) && existing != registration.JobType)
            throw new InvalidOperationException($"Job type name \"{registration.Name}\" is already registered for \"{existing.FullName}\".");

        _nameToType[registration.Name] = registration.JobType;
        _typeToName[registration.JobType] = registration.Name;
    }
}

public sealed class JobHandle
{
    private readonly IJobMonitor _monitor;
    private readonly Func<string, CancellationToken, Task<bool>> _requestCancellation;

    internal JobHandle(string jobId, IJobMonitor monitor, Func<string, CancellationToken, Task<bool>> requestCancellation)
    {
        JobId = jobId;
        _monitor = monitor;
        _requestCancellation = requestCancellation;
    }

    public string JobId { get; }

    public Task<JobState?> GetStateAsync(CancellationToken cancellationToken = default)
    {
        return _monitor.GetAsync(JobId, cancellationToken);
    }

    public Task<bool> RequestCancellationAsync(CancellationToken cancellationToken = default)
    {
        return _requestCancellation(JobId, cancellationToken);
    }
}

/// <summary>
/// Passed to a durable job that implements <see cref="IJobWithExecutionContext"/>. Gives the running job its identity
/// and attempt number, plus store-backed progress reporting, lease heartbeat (for long runs), and cooperative
/// cancellation checks — the parts of <see cref="IJobRuntimeStore"/> that are useful from inside job code.
/// </summary>
public sealed class JobExecutionContext
{
    private readonly IJobRuntimeStore _store;
    private readonly string _nodeId;
    private readonly TimeSpan _lease;

    internal JobExecutionContext(string jobId, int attempt, CancellationToken cancellationToken, IJobRuntimeStore store, string nodeId, TimeSpan lease)
    {
        JobId = jobId;
        Attempt = attempt;
        CancellationToken = cancellationToken;
        _store = store;
        _nodeId = nodeId;
        _lease = lease;
    }

    public string JobId { get; }
    public int Attempt { get; }
    public CancellationToken CancellationToken { get; }

    public Task ReportProgressAsync(int? percent = null, string? message = null, CancellationToken cancellationToken = default)
        => _store.SetProgressAsync(JobId, percent, message, cancellationToken);

    // Extends the worker's lease so a long-but-alive run is not reclaimed as stale.
    public Task<bool> RenewLeaseAsync(CancellationToken cancellationToken = default)
        => _store.RenewClaimAsync(JobId, _nodeId, _lease, cancellationToken);

    public Task<bool> IsCancellationRequestedAsync(CancellationToken cancellationToken = default)
        => _store.IsCancellationRequestedAsync(JobId, cancellationToken);
}

public interface IJobMonitor
{
    Task<JobState?> GetAsync(string jobId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<JobState>> QueryAsync(JobQuery query, CancellationToken cancellationToken = default);
}

public interface IJobClient
{
    Task<JobHandle> EnqueueAsync<TJob>(JobRequestOptions? options = null, CancellationToken cancellationToken = default) where TJob : IJob;
    Task<JobHandle> EnqueueAsync(Type jobType, JobRequestOptions? options = null, CancellationToken cancellationToken = default);
    Task<bool> RequestCancellationAsync(string jobId, CancellationToken cancellationToken = default);
}

public interface IJobWorker
{
    Task<bool> RunAsync(string jobId, CancellationToken cancellationToken = default);
    Task<int> RunQueuedAsync(int limit = 100, CancellationToken cancellationToken = default);
    // Reclaims jobs stuck in Processing past their lease (a worker that crashed mid-run): re-queues them while attempts
    // remain, otherwise dead-letters them. Returns the number recovered.
    Task<int> RecoverStaleAsync(int maxAttempts, int limit = 100, CancellationToken cancellationToken = default);
}

public interface IJobRuntimeStore : IJobMonitor
{
    Task CreateIfAbsentAsync(JobState initial, CancellationToken cancellationToken = default);
    // When expectedNodeId is non-null, the transition only succeeds if the job is currently owned by that node.
    // Worker terminal transitions pass their node id so a stale worker whose lease was reclaimed cannot overwrite
    // the new owner's state.
    Task<bool> TryTransitionAsync(string jobId, JobStatus expectedStatus, JobStatus newStatus, JobStatePatch? patch = null, string? expectedNodeId = null, CancellationToken cancellationToken = default);
    Task<bool> TryClaimAsync(string jobId, string nodeId, TimeSpan lease, CancellationToken cancellationToken = default);
    Task<bool> RenewClaimAsync(string jobId, string nodeId, TimeSpan lease, CancellationToken cancellationToken = default);
    Task<bool> ReleaseClaimAsync(string jobId, string nodeId, CancellationToken cancellationToken = default);
    // Returns plain (non-CRON-occurrence) jobs in Processing whose lease has expired as of <paramref name="now"/>
    // (their owning worker is presumed dead), so the runtime can reclaim them. CRON occurrences are excluded — the
    // scheduler recovers those with its own per-definition retry budget.
    Task<IReadOnlyList<JobState>> GetExpiredProcessingAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken = default);
    // Atomically reclaims a stale Processing job: the transition applies only if the job is STILL owned by
    // <paramref name="expectedNodeId"/> and its lease is STILL expired as of <paramref name="now"/>. This closes the
    // race where the owning worker renews its lease between a stale scan and the reclaim (which would otherwise
    // re-queue a live job and double-run it).
    Task<bool> TryReclaimExpiredAsync(string jobId, DateTimeOffset now, string expectedNodeId, JobStatus newStatus, JobStatePatch? patch = null, CancellationToken cancellationToken = default);
    Task SetProgressAsync(string jobId, int? percent = null, string? message = null, CancellationToken cancellationToken = default);
    Task IncrementAttemptAsync(string jobId, CancellationToken cancellationToken = default);
    Task<bool> RequestCancellationAsync(string jobId, CancellationToken cancellationToken = default);
    Task<bool> IsCancellationRequestedAsync(string jobId, CancellationToken cancellationToken = default);
    Task ScheduleDispatchAsync(ScheduledDispatchState dispatch, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ScheduledDispatchState>> ClaimDueDispatchesAsync(DateTimeOffset now, int limit, string nodeId, TimeSpan lease, CancellationToken cancellationToken = default);
    Task CompleteDispatchAsync(string dispatchId, string nodeId, CancellationToken cancellationToken = default);
    Task ReleaseDispatchAsync(string dispatchId, string nodeId, DateTimeOffset nextDueUtc, CancellationToken cancellationToken = default);
}

public sealed class InMemoryJobRuntimeStore : IJobRuntimeStore
{
    private readonly ConcurrentDictionary<string, JobState> _jobs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ScheduledDispatchState> _dispatches = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly object _lock = new();

    public InMemoryJobRuntimeStore(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task CreateIfAbsentAsync(JobState initial, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(initial);
        cancellationToken.ThrowIfCancellationRequested();

        var now = _timeProvider.GetUtcNow();
        _jobs.TryAdd(initial.JobId, initial with
        {
            CreatedUtc = initial.CreatedUtc == default ? now : initial.CreatedUtc,
            LastUpdatedUtc = initial.LastUpdatedUtc == default ? now : initial.LastUpdatedUtc
        });

        return Task.CompletedTask;
    }

    public Task<JobState?> GetAsync(string jobId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _jobs.TryGetValue(jobId, out var state);
        return Task.FromResult(state);
    }

    public Task<IReadOnlyList<JobState>> QueryAsync(JobQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        IEnumerable<JobState> results = _jobs.Values;
        if (!String.IsNullOrEmpty(query.Name))
            results = results.Where(s => String.Equals(s.Name, query.Name, StringComparison.Ordinal));

        if (query.Status is { } status)
            results = results.Where(s => s.Status == status);

        if (query.ExcludeOccurrences)
            results = results.Where(s => s.ScheduledForUtc is null);

        return Task.FromResult<IReadOnlyList<JobState>>(results
            .OrderByDescending(s => s.LastUpdatedUtc)
            .Take(Math.Max(1, query.Limit))
            .ToArray());
    }

    public Task<bool> TryTransitionAsync(string jobId, JobStatus expectedStatus, JobStatus newStatus, JobStatePatch? patch = null, string? expectedNodeId = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_lock)
        {
            if (!_jobs.TryGetValue(jobId, out var current) || current.Status != expectedStatus)
                return Task.FromResult(false);

            if (expectedNodeId is not null && !String.Equals(current.NodeId, expectedNodeId, StringComparison.Ordinal))
                return Task.FromResult(false);

            _jobs[jobId] = ApplyPatch(current, patch) with
            {
                Status = newStatus,
                LastUpdatedUtc = patch?.LastUpdatedUtc ?? _timeProvider.GetUtcNow()
            };
            return Task.FromResult(true);
        }
    }

    public Task<bool> TryClaimAsync(string jobId, string nodeId, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrEmpty(nodeId);

        lock (_lock)
        {
            if (!_jobs.TryGetValue(jobId, out var current))
                return Task.FromResult(false);

            var now = _timeProvider.GetUtcNow();
            if (!String.IsNullOrEmpty(current.NodeId) && current.LeaseExpiresUtc is { } leaseExpires && leaseExpires > now && current.NodeId != nodeId)
                return Task.FromResult(false);

            _jobs[jobId] = current with
            {
                NodeId = nodeId,
                LeaseExpiresUtc = now.Add(lease),
                LastUpdatedUtc = now
            };
            return Task.FromResult(true);
        }
    }

    public Task<bool> RenewClaimAsync(string jobId, string nodeId, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_lock)
        {
            if (!_jobs.TryGetValue(jobId, out var current) || current.NodeId != nodeId)
                return Task.FromResult(false);

            var now = _timeProvider.GetUtcNow();
            _jobs[jobId] = current with
            {
                LeaseExpiresUtc = now.Add(lease),
                LastUpdatedUtc = now
            };
            return Task.FromResult(true);
        }
    }

    public Task<bool> ReleaseClaimAsync(string jobId, string nodeId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_lock)
        {
            if (!_jobs.TryGetValue(jobId, out var current) || current.NodeId != nodeId)
                return Task.FromResult(false);

            _jobs[jobId] = current with
            {
                NodeId = null,
                LeaseExpiresUtc = null,
                LastUpdatedUtc = _timeProvider.GetUtcNow()
            };
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<JobState>> GetExpiredProcessingAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_lock)
        {
            var expired = _jobs.Values
                // Exclude CRON occurrences (ScheduledForUtc set): the scheduler owns their recovery via its own
                // per-definition retry budget. This path only recovers plain IJobClient-submitted jobs.
                .Where(s => s.Status == JobStatus.Processing && s.ScheduledForUtc is null && s.LeaseExpiresUtc is { } lease && lease <= now)
                .OrderBy(s => s.LeaseExpiresUtc)
                .Take(Math.Max(1, limit))
                .ToArray();

            return Task.FromResult<IReadOnlyList<JobState>>(expired);
        }
    }

    public Task<bool> TryReclaimExpiredAsync(string jobId, DateTimeOffset now, string expectedNodeId, JobStatus newStatus, JobStatePatch? patch = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrEmpty(expectedNodeId);

        lock (_lock)
        {
            if (!_jobs.TryGetValue(jobId, out var current))
                return Task.FromResult(false);

            // Re-check (atomically, under the lock) the conditions the stale scan saw: still Processing, still owned by
            // the same node, and the lease is still expired. A renewal or re-claim that landed since the scan fails one
            // of these and the reclaim is skipped.
            if (current.Status != JobStatus.Processing || !String.Equals(current.NodeId, expectedNodeId, StringComparison.Ordinal))
                return Task.FromResult(false);

            if (current.LeaseExpiresUtc is not { } lease || lease > now)
                return Task.FromResult(false);

            _jobs[jobId] = ApplyPatch(current, patch) with
            {
                Status = newStatus,
                LastUpdatedUtc = patch?.LastUpdatedUtc ?? _timeProvider.GetUtcNow()
            };
            return Task.FromResult(true);
        }
    }

    public Task SetProgressAsync(string jobId, int? percent = null, string? message = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        UpdateJob(jobId, state => state with
        {
            Progress = percent ?? state.Progress,
            ProgressMessage = message ?? state.ProgressMessage,
            LastUpdatedUtc = _timeProvider.GetUtcNow()
        });

        return Task.CompletedTask;
    }

    public Task IncrementAttemptAsync(string jobId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        UpdateJob(jobId, state => state with { Attempt = state.Attempt + 1, LastUpdatedUtc = _timeProvider.GetUtcNow() });
        return Task.CompletedTask;
    }

    public Task<bool> RequestCancellationAsync(string jobId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(UpdateJob(jobId, state => state with
        {
            CancellationRequested = true,
            LastUpdatedUtc = _timeProvider.GetUtcNow()
        }));
    }

    public Task<bool> IsCancellationRequestedAsync(string jobId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_jobs.TryGetValue(jobId, out var state) && state.CancellationRequested);
    }

    public Task ScheduleDispatchAsync(ScheduledDispatchState dispatch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        cancellationToken.ThrowIfCancellationRequested();
        _dispatches.TryAdd(dispatch.DispatchId, dispatch);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ScheduledDispatchState>> ClaimDueDispatchesAsync(DateTimeOffset now, int limit, string nodeId, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrEmpty(nodeId);

        lock (_lock)
        {
            var due = _dispatches.Values
                .Where(d => d.DueUtc <= now && (String.IsNullOrEmpty(d.ClaimOwner) || d.ClaimExpiresUtc <= now))
                .OrderBy(d => d.DueUtc)
                .Take(Math.Max(1, limit))
                .ToArray();

            for (int index = 0; index < due.Length; index++)
            {
                var claimed = due[index] with
                {
                    ClaimOwner = nodeId,
                    ClaimExpiresUtc = now.Add(lease),
                    Attempts = due[index].Attempts + 1
                };
                _dispatches[claimed.DispatchId] = claimed;
                due[index] = claimed;
            }

            return Task.FromResult<IReadOnlyList<ScheduledDispatchState>>(due);
        }
    }

    public Task CompleteDispatchAsync(string dispatchId, string nodeId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_lock)
        {
            if (_dispatches.TryGetValue(dispatchId, out var dispatch) && dispatch.ClaimOwner == nodeId)
                _dispatches.TryRemove(dispatchId, out _);
        }

        return Task.CompletedTask;
    }

    public Task ReleaseDispatchAsync(string dispatchId, string nodeId, DateTimeOffset nextDueUtc, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_lock)
        {
            if (_dispatches.TryGetValue(dispatchId, out var dispatch) && dispatch.ClaimOwner == nodeId)
            {
                _dispatches[dispatchId] = dispatch with
                {
                    DueUtc = nextDueUtc,
                    ClaimOwner = null,
                    ClaimExpiresUtc = null
                };
            }
        }

        return Task.CompletedTask;
    }

    private bool UpdateJob(string jobId, Func<JobState, JobState> update)
    {
        lock (_lock)
        {
            if (!_jobs.TryGetValue(jobId, out var current))
                return false;

            _jobs[jobId] = update(current);
            return true;
        }
    }

    private JobState ApplyPatch(JobState state, JobStatePatch? patch)
    {
        if (patch is null)
            return state;

        return state with
        {
            Status = patch.Status ?? state.Status,
            JobType = patch.JobType ?? state.JobType,
            Progress = patch.Progress ?? state.Progress,
            ProgressMessage = patch.ProgressMessage ?? state.ProgressMessage,
            Error = patch.Error ?? state.Error,
            Attempt = state.Attempt + patch.AttemptDelta,
            NodeId = patch.ClearNodeId ? null : patch.NodeId ?? state.NodeId,
            LeaseExpiresUtc = patch.ClearLeaseExpiresUtc ? null : patch.LeaseExpiresUtc ?? state.LeaseExpiresUtc,
            LastUpdatedUtc = patch.LastUpdatedUtc ?? state.LastUpdatedUtc,
            StartedUtc = patch.StartedUtc ?? state.StartedUtc,
            CompletedUtc = patch.CompletedUtc ?? state.CompletedUtc,
            CancellationRequested = patch.CancellationRequested ?? state.CancellationRequested
        };
    }
}

public sealed class JobClient : IJobClient
{
    private readonly IJobRuntimeStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly IJobTypeRegistry _jobTypes;

    public JobClient(IJobRuntimeStore store, TimeProvider? timeProvider = null, IJobTypeRegistry? jobTypes = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _jobTypes = jobTypes ?? new JobTypeRegistry();
    }

    public Task<JobHandle> EnqueueAsync<TJob>(JobRequestOptions? options = null, CancellationToken cancellationToken = default) where TJob : IJob
    {
        return EnqueueAsync(typeof(TJob), options, cancellationToken);
    }

    public async Task<JobHandle> EnqueueAsync(Type jobType, JobRequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jobType);
        if (!typeof(IJob).IsAssignableFrom(jobType))
            throw new ArgumentException("Job type must implement IJob.", nameof(jobType));

        options ??= new JobRequestOptions();
        string jobId = options.JobId ?? Guid.NewGuid().ToString("N");
        string name = options.Name ?? jobType.Name;
        var now = _timeProvider.GetUtcNow();

        await _store.CreateIfAbsentAsync(new JobState
        {
            JobId = jobId,
            Name = name,
            JobType = _jobTypes.GetName(jobType),
            Status = JobStatus.Queued,
            CreatedUtc = now,
            LastUpdatedUtc = now
        }, cancellationToken).ConfigureAwait(false);

        return new JobHandle(jobId, _store, RequestCancellationAsync);
    }

    public Task<bool> RequestCancellationAsync(string jobId, CancellationToken cancellationToken = default)
    {
        return _store.RequestCancellationAsync(jobId, cancellationToken);
    }
}

/// <summary>
/// Resolves a stable, process-unique node identity used for job claims and per-node scheduling.
/// Honors the <c>FOUNDATIO_NODE_ID</c> environment variable when set; otherwise combines machine name,
/// process id, and a process-lifetime token so co-located worker processes do not collapse to one identity.
/// </summary>
internal static class NodeIdentity
{
    public static string Current { get; } = Resolve();

    private static string Resolve()
    {
        string? configured = Environment.GetEnvironmentVariable("FOUNDATIO_NODE_ID");
        if (!String.IsNullOrEmpty(configured))
            return configured;

        return $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid().ToString("N")[..8]}";
    }
}

public sealed class JobWorker : IJobWorker
{
    private static readonly TimeSpan DefaultLease = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultCancellationPollInterval = TimeSpan.FromSeconds(1);

    private readonly IJobRuntimeStore _store;
    private readonly IServiceProvider _serviceProvider;
    private readonly TimeProvider _timeProvider;
    private readonly IJobTypeRegistry _jobTypes;
    private readonly string _nodeId;
    private readonly TimeSpan _lease;
    private readonly TimeSpan _cancellationPollInterval;

    public JobWorker(IJobRuntimeStore store, IServiceProvider serviceProvider, TimeProvider? timeProvider = null, string? nodeId = null, TimeSpan? lease = null, IJobTypeRegistry? jobTypes = null, TimeSpan? cancellationPollInterval = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _jobTypes = jobTypes ?? new JobTypeRegistry();
        _nodeId = !String.IsNullOrEmpty(nodeId) ? nodeId : NodeIdentity.Current;
        _lease = lease ?? DefaultLease;

        // Cooperative cancellation is observed by polling the runtime store. The default is intentionally
        // conservative (one poll per second per running job) so a real store isn't hammered when many jobs run
        // concurrently; callers that need snappier cancellation can opt into a tighter interval.
        var pollInterval = cancellationPollInterval ?? DefaultCancellationPollInterval;
        _cancellationPollInterval = pollInterval > TimeSpan.Zero ? pollInterval : DefaultCancellationPollInterval;
    }

    public async Task<int> RunQueuedAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        var queued = await _store.QueryAsync(new JobQuery
        {
            Status = JobStatus.Queued,
            Limit = limit,
            // The scheduler owns CRON occurrences (retry/dead-letter accounting); the generic worker must skip them.
            ExcludeOccurrences = true
        }, cancellationToken).ConfigureAwait(false);

        int completed = 0;
        foreach (var state in queued)
        {
            if (await RunJobStateAsync(state, cancellationToken).ConfigureAwait(false))
                completed++;
        }

        return completed;
    }

    public async Task<bool> RunAsync(string jobId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);

        var state = await _store.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
        return state is not null && await RunJobStateAsync(state, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> RecoverStaleAsync(int maxAttempts, int limit = 100, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var stale = await _store.GetExpiredProcessingAsync(now, limit, cancellationToken).ConfigureAwait(false);

        int recovered = 0;
        foreach (var state in stale)
        {
            if (String.IsNullOrEmpty(state.NodeId))
                continue;

            // TryReclaimExpiredAsync re-verifies (atomically) that the job is still owned by the same presumed-dead
            // node and its lease is still expired, so a worker that renewed between the scan and here is not yanked out
            // from under itself (no double-run). Attempts are incremented per run, so a job that keeps crashing is
            // dead-lettered once it has consumed its attempt budget instead of being re-queued forever.
            //
            // Budget semantics for ad-hoc (IJobClient) jobs: `maxAttempts` is the TOTAL number of attempts, so
            // dead-letter at Attempt >= maxAttempts. (CRON occurrences use a different knob — ScheduledJobDefinition
            // .MaxRetries, the number of retries AFTER the first run, i.e. total runs = MaxRetries + 1 — and are
            // excluded from this path via GetExpiredProcessingAsync; the scheduler owns their recovery.)
            bool transitioned = state.Attempt >= maxAttempts
                ? await _store.TryReclaimExpiredAsync(state.JobId, now, state.NodeId, JobStatus.DeadLettered, new JobStatePatch
                {
                    Error = $"Lease expired after {state.Attempt} attempt(s) without completion.",
                    ClearNodeId = true,
                    ClearLeaseExpiresUtc = true,
                    CompletedUtc = now,
                    LastUpdatedUtc = now
                }, cancellationToken).ConfigureAwait(false)
                : await _store.TryReclaimExpiredAsync(state.JobId, now, state.NodeId, JobStatus.Queued, new JobStatePatch
                {
                    ClearNodeId = true,
                    ClearLeaseExpiresUtc = true,
                    LastUpdatedUtc = now
                }, cancellationToken).ConfigureAwait(false);

            if (transitioned)
                recovered++;
        }

        return recovered;
    }

    private async Task<bool> RunJobStateAsync(JobState state, CancellationToken cancellationToken)
    {
        if (state.Status != JobStatus.Queued)
            return false;

        var now = _timeProvider.GetUtcNow();
        if (!await _store.TryTransitionAsync(state.JobId, JobStatus.Queued, JobStatus.Processing, new JobStatePatch
        {
            NodeId = _nodeId,
            StartedUtc = now,
            LeaseExpiresUtc = now.Add(_lease),
            AttemptDelta = 1
        }, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var jobTag = new KeyValuePair<string, object?>("job", state.Name);
        JobInstruments.Started.Add(1, jobTag);

        using var linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var cancellationWatcher = WatchCancellation(state.JobId, linkedCancellationTokenSource);
        using var leaseRenewer = RenewLeasePeriodically(state.JobId, linkedCancellationTokenSource);

        try
        {
            var jobType = ResolveJobType(state);
            var job = (IJob)ActivatorUtilities.GetServiceOrCreateInstance(_serviceProvider, jobType);

            // Hand the job its execution context (progress, heartbeat, cancellation, identity) when it opts in. The
            // store was already incremented to this attempt by the Queued -> Processing transition above.
            if (job is IJobWithExecutionContext contextual)
                contextual.ExecutionContext = new JobExecutionContext(state.JobId, state.Attempt + 1, linkedCancellationTokenSource.Token, _store, _nodeId, _lease);

            var result = await job.TryRunAsync(linkedCancellationTokenSource.Token).ConfigureAwait(false);
            var completedAt = _timeProvider.GetUtcNow();

            if (result.IsCancelled)
            {
                await _store.TryTransitionAsync(state.JobId, JobStatus.Processing, JobStatus.Cancelled, new JobStatePatch
                {
                    Error = result.Message,
                    CompletedUtc = completedAt,
                    ClearNodeId = true,
                    ClearLeaseExpiresUtc = true
                }, expectedNodeId: _nodeId, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
            else if (result.IsSuccess)
            {
                await _store.TryTransitionAsync(state.JobId, JobStatus.Processing, JobStatus.Completed, new JobStatePatch
                {
                    CompletedUtc = completedAt,
                    ClearNodeId = true,
                    ClearLeaseExpiresUtc = true,
                    Progress = 100
                }, expectedNodeId: _nodeId, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await _store.TryTransitionAsync(state.JobId, JobStatus.Processing, JobStatus.Failed, new JobStatePatch
                {
                    Error = result.Message,
                    CompletedUtc = completedAt,
                    ClearNodeId = true,
                    ClearLeaseExpiresUtc = true
                }, expectedNodeId: _nodeId, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }

            if (result.IsCancelled)
                JobInstruments.Cancelled.Add(1, jobTag);
            else if (result.IsSuccess)
                JobInstruments.Completed.Add(1, jobTag);
            else
                JobInstruments.Failed.Add(1, jobTag);

            JobInstruments.RunTime.Record((completedAt - now).TotalMilliseconds, jobTag);
            return true;
        }
        catch (Exception ex)
        {
            var failedAt = _timeProvider.GetUtcNow();
            await _store.TryTransitionAsync(state.JobId, JobStatus.Processing, JobStatus.Failed, new JobStatePatch
            {
                Error = ex.Message,
                CompletedUtc = failedAt,
                ClearNodeId = true,
                ClearLeaseExpiresUtc = true
            }, expectedNodeId: _nodeId, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            JobInstruments.Failed.Add(1, jobTag);
            JobInstruments.RunTime.Record((failedAt - now).TotalMilliseconds, jobTag);
            throw;
        }
    }

    private Type ResolveJobType(JobState state)
    {
        if (String.IsNullOrEmpty(state.JobType))
            throw new InvalidOperationException($"Job \"{state.JobId}\" does not have a job type and cannot be executed by a worker.");

        try
        {
            return _jobTypes.Resolve(state.JobType);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            throw new InvalidOperationException($"Job type \"{state.JobType}\" for job \"{state.JobId}\" could not be resolved to an IJob implementation.", ex);
        }
    }

    private IDisposable WatchCancellation(string jobId, CancellationTokenSource cancellationTokenSource)
    {
        return new Timer(_ => _ = PollCancellationAsync(jobId, cancellationTokenSource), null, _cancellationPollInterval, _cancellationPollInterval);
    }

    private IDisposable RenewLeasePeriodically(string jobId, CancellationTokenSource cancellationTokenSource)
    {
        // Renew well before the lease elapses so a slow-but-alive worker keeps ownership and is not reclaimed.
        var interval = TimeSpan.FromMilliseconds(Math.Max(250, _lease.TotalMilliseconds / 3));
        return new Timer(_ => _ = RenewLeaseAsync(jobId, cancellationTokenSource), null, interval, interval);
    }

    private async Task RenewLeaseAsync(string jobId, CancellationTokenSource cancellationTokenSource)
    {
        if (cancellationTokenSource.IsCancellationRequested)
            return;

        try
        {
            // If renewal fails the lease was lost to another node; cancel the run so this worker stops and
            // its terminal transition (guarded by expectedNodeId) cannot overwrite the new owner's state.
            if (!await _store.RenewClaimAsync(jobId, _nodeId, _lease, CancellationToken.None).ConfigureAwait(false))
                await cancellationTokenSource.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task PollCancellationAsync(string jobId, CancellationTokenSource cancellationTokenSource)
    {
        if (cancellationTokenSource.IsCancellationRequested)
            return;

        try
        {
            if (await _store.IsCancellationRequestedAsync(jobId, CancellationToken.None).ConfigureAwait(false))
                await cancellationTokenSource.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
