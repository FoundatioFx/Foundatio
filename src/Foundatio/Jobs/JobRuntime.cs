using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace Foundatio.Jobs;

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
    public int? Progress { get; init; }
    public string? ProgressMessage { get; init; }
    public string? Error { get; init; }
    public int AttemptDelta { get; init; }
    public string? NodeId { get; init; }
    public DateTimeOffset? LeaseExpiresUtc { get; init; }
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

public sealed record RunJobOptions
{
    public string? JobId { get; init; }
    public string? Name { get; init; }
    public string? NodeId { get; init; }
}

public interface IJobMonitor
{
    Task<JobState?> GetAsync(string jobId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<JobState>> QueryAsync(JobQuery query, CancellationToken cancellationToken = default);
}

public interface IJobClient : IJobMonitor
{
    Task<string> RunAsync<TJob>(RunJobOptions? options = null, CancellationToken cancellationToken = default) where TJob : IJob;
    Task<bool> RequestCancellationAsync(string jobId, CancellationToken cancellationToken = default);
}

public interface IJobRuntimeStore : IJobMonitor
{
    Task CreateIfAbsentAsync(JobState initial, CancellationToken cancellationToken = default);
    Task<bool> TryTransitionAsync(string jobId, JobStatus expectedStatus, JobStatus newStatus, JobStatePatch? patch = null, CancellationToken cancellationToken = default);
    Task<bool> TryClaimAsync(string jobId, string nodeId, TimeSpan lease, CancellationToken cancellationToken = default);
    Task<bool> RenewClaimAsync(string jobId, string nodeId, TimeSpan lease, CancellationToken cancellationToken = default);
    Task<bool> ReleaseClaimAsync(string jobId, string nodeId, CancellationToken cancellationToken = default);
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

        return Task.FromResult<IReadOnlyList<JobState>>(results
            .OrderByDescending(s => s.LastUpdatedUtc)
            .Take(Math.Max(1, query.Limit))
            .ToArray());
    }

    public Task<bool> TryTransitionAsync(string jobId, JobStatus expectedStatus, JobStatus newStatus, JobStatePatch? patch = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_lock)
        {
            if (!_jobs.TryGetValue(jobId, out var current) || current.Status != expectedStatus)
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
            Progress = patch.Progress ?? state.Progress,
            ProgressMessage = patch.ProgressMessage ?? state.ProgressMessage,
            Error = patch.Error ?? state.Error,
            Attempt = state.Attempt + patch.AttemptDelta,
            NodeId = patch.NodeId ?? state.NodeId,
            LeaseExpiresUtc = patch.LeaseExpiresUtc ?? state.LeaseExpiresUtc,
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
    private readonly IServiceProvider _serviceProvider;
    private readonly TimeProvider _timeProvider;
    private readonly string _nodeId;

    public JobClient(IJobRuntimeStore store, IServiceProvider serviceProvider, TimeProvider? timeProvider = null, string? nodeId = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _nodeId = !String.IsNullOrEmpty(nodeId)
            ? nodeId
            : Environment.GetEnvironmentVariable("FOUNDATIO_NODE_ID") ?? Environment.MachineName;
    }

    public Task<JobState?> GetAsync(string jobId, CancellationToken cancellationToken = default)
    {
        return _store.GetAsync(jobId, cancellationToken);
    }

    public Task<IReadOnlyList<JobState>> QueryAsync(JobQuery query, CancellationToken cancellationToken = default)
    {
        return _store.QueryAsync(query, cancellationToken);
    }

    public async Task<string> RunAsync<TJob>(RunJobOptions? options = null, CancellationToken cancellationToken = default) where TJob : IJob
    {
        options ??= new RunJobOptions();
        string jobId = options.JobId ?? Guid.NewGuid().ToString("N");
        string name = options.Name ?? typeof(TJob).Name;
        string nodeId = options.NodeId ?? _nodeId;
        var now = _timeProvider.GetUtcNow();

        await _store.CreateIfAbsentAsync(new JobState
        {
            JobId = jobId,
            Name = name,
            Status = JobStatus.Queued,
            CreatedUtc = now,
            LastUpdatedUtc = now
        }, cancellationToken).ConfigureAwait(false);

        if (!await _store.TryTransitionAsync(jobId, JobStatus.Queued, JobStatus.Processing, new JobStatePatch
        {
            NodeId = nodeId,
            StartedUtc = now,
            LeaseExpiresUtc = now.AddMinutes(5),
            AttemptDelta = 1
        }, cancellationToken).ConfigureAwait(false))
        {
            return jobId;
        }

        using var linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var cancellationWatcher = WatchCancellation(jobId, linkedCancellationTokenSource);
        var job = ActivatorUtilities.GetServiceOrCreateInstance<TJob>(_serviceProvider);

        try
        {
            var result = await job.TryRunAsync(linkedCancellationTokenSource.Token).ConfigureAwait(false);
            var completedAt = _timeProvider.GetUtcNow();
            if (result.IsCancelled)
            {
                await _store.TryTransitionAsync(jobId, JobStatus.Processing, JobStatus.Cancelled, new JobStatePatch
                {
                    Error = result.Message,
                    CompletedUtc = completedAt,
                    LeaseExpiresUtc = null
                }, CancellationToken.None).ConfigureAwait(false);
            }
            else if (result.IsSuccess)
            {
                await _store.TryTransitionAsync(jobId, JobStatus.Processing, JobStatus.Completed, new JobStatePatch
                {
                    CompletedUtc = completedAt,
                    LeaseExpiresUtc = null,
                    Progress = 100
                }, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await _store.TryTransitionAsync(jobId, JobStatus.Processing, JobStatus.Failed, new JobStatePatch
                {
                    Error = result.Message,
                    CompletedUtc = completedAt,
                    LeaseExpiresUtc = null
                }, CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            await _store.ReleaseClaimAsync(jobId, nodeId, CancellationToken.None).ConfigureAwait(false);
        }

        return jobId;
    }

    public Task<bool> RequestCancellationAsync(string jobId, CancellationToken cancellationToken = default)
    {
        return _store.RequestCancellationAsync(jobId, cancellationToken);
    }

    private IDisposable WatchCancellation(string jobId, CancellationTokenSource cancellationTokenSource)
    {
        return new Timer(_ => _ = PollCancellationAsync(jobId, cancellationTokenSource), null, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50));
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
