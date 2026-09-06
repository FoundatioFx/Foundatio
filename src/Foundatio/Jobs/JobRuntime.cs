using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Foundatio.Serializer;
using Foundatio.Utility;

namespace Foundatio.Jobs;

/// <summary>
/// Core-owned durable-job instruments, shared by every <see cref="JobWorker"/> so job throughput and run latency are
/// observable independent of the runtime store implementation.
/// </summary>
internal static class JobInstruments
{
    public static readonly Counter<long> CapacityRejected = FoundatioDiagnostics.Meter.CreateCounter<long>("foundatio.jobs.capacity_rejected", description: "Job store admissions rejected by a configured resource budget");
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
    PubSubMessage
}

public sealed record JobState
{
    public required string JobId { get; init; }
    public required string Name { get; init; }
    public string? JobType { get; init; }

    /// <summary>Serialized per-invocation arguments (see <see cref="IJobClient.EnqueueAsync{TJob, TArgs}"/>); null when the job takes none.</summary>
    public ReadOnlyMemory<byte>? Payload { get; init; }

    /// <summary>Discriminator for the payload type (the argument type's full name), stored for forensics and mismatch diagnostics.</summary>
    public string? PayloadType { get; init; }
    public JobStatus Status { get; init; } = JobStatus.Queued;
    public int? Progress { get; init; }
    public string? ProgressMessage { get; init; }
    public int Attempt { get; init; }
    /// <summary>Total execution attempts allowed, including retries and crash recovery.</summary>
    public int MaxAttempts { get; init; } = 3;
    public JobRetryPolicy RetryPolicy { get; init; } = new();
    /// <summary>Earliest execution time, including persisted retry delays.</summary>
    public DateTimeOffset? AvailableUtc { get; init; }
    /// <summary>Unique ownership token for the current execution; changes on every claim.</summary>
    public string? ClaimToken { get; init; }
    /// <summary>Optional node affinity for per-node scheduled work.</summary>
    public string? RequiredNodeId { get; init; }
    /// <summary>Schedule that created this occurrence; null for ad hoc jobs.</summary>
    public string? ScheduleName { get; init; }
    public string? NodeId { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset LastUpdatedUtc { get; init; }
    public DateTimeOffset? StartedUtc { get; init; }
    public DateTimeOffset? CompletedUtc { get; init; }
    public DateTimeOffset? LeaseExpiresUtc { get; init; }
    public string? Error { get; init; }
    /// <summary>Informational outcome, independent of failure diagnostics.</summary>
    public string? ResultMessage { get; init; }
    public bool CancellationRequested { get; init; }
    public DateTimeOffset? ScheduledForUtc { get; init; }
    /// <summary>Expires an unclaimed per-node occurrence when its intended node never returns.</summary>
    public DateTimeOffset? ExpiresUtc { get; init; }
}

public sealed record JobQuery
{
    public string? Name { get; init; }
    public JobStatus? Status { get; init; }
    public int Limit { get; init; } = 100;

    /// <summary>Continue after the token returned by the preceding page, using the same filters.</summary>
    public string? AfterJobId { get; init; }

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(Limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Limit, 1000);
    }

}

public sealed record ScheduledDispatchState
{
    public required string DispatchId { get; init; }
    public ScheduledDispatchKind Kind { get; init; }

    /// <summary>The transport destination for a queue message or publication.</summary>
    public DestinationAddress? Destination { get; init; }

    public required ReadOnlyMemory<byte> Body { get; init; }
    public MessageHeaders Headers { get; init; } = MessageHeaders.Empty;
    public TransportSendOptions Options { get; init; } = new();
    public DateTimeOffset DueUtc { get; init; }
    public string? ClaimOwner { get; init; }
    public DateTimeOffset? ClaimExpiresUtc { get; init; }
    public int Attempts { get; init; }
}

/// <summary>Distinguishes a confirmed occurrence from overlap that may become eligible later.</summary>
public enum JobOccurrenceResult { AlreadyExists, Created, OverlapBlocked }

public sealed record JobRequestOptions
{
    /// <summary>Relative initial delay. Mutually exclusive with RunAt.</summary>
    public TimeSpan? Delay { get; init; }
    /// <summary>Absolute earliest execution time. Mutually exclusive with Delay.</summary>
    public DateTimeOffset? RunAt { get; init; }
    /// <summary>Total execution attempts, including retries. Default three.</summary>
    public int MaxAttempts { get; init; } = 3;
    public JobRetryPolicy RetryPolicy { get; init; } = new();
    public string? JobId { get; init; }
    public string? Name { get; init; }
}

public sealed record JobTypeRegistration(string Name, Type JobType);

public interface IJobTypeRegistry
{
    IReadOnlyCollection<string> Names { get; }
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

    public IReadOnlyCollection<string> Names => _nameToType.Keys;

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

        throw new JobException($"Job type \"{name}\" is not registered. Register each worker job with AddFoundatio().Jobs.AddJobType<TJob>().");
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
    private readonly TimeProvider _timeProvider;
    private readonly Func<string, CancellationToken, Task<bool>> _requestCancellation;

    internal JobHandle(string jobId, IJobMonitor monitor, Func<string, CancellationToken, Task<bool>> requestCancellation, TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        JobId = jobId;
        _monitor = monitor;
        _requestCancellation = requestCancellation;
    }

    public string JobId { get; }

    public Task<JobState?> GetStateAsync(CancellationToken cancellationToken = default)
    {
        return _monitor.GetAsync(JobId, cancellationToken);
    }

    /// <summary>Waits for a terminal state. Timeout or cancellation stops this wait without cancelling the job.</summary>
    public async Task<JobState> WaitForCompletionAsync(TimeSpan? timeout = null, TimeSpan? pollInterval = null, CancellationToken cancellationToken = default)
    {
        var duration = timeout ?? TimeSpan.FromMinutes(5);
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(250);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        using var deadline = new CancellationTokenSource(duration, _timeProvider);
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            while (true)
            {
                waiting.Token.ThrowIfCancellationRequested();
                var state = await _monitor.GetAsync(JobId, waiting.Token).WaitAsync(waiting.Token).AnyContext()
                    ?? throw new JobException($"Job {JobId} is unavailable; its retained history may have expired.");
                if (state.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled or JobStatus.DeadLettered)
                    return state;
                await Task.Delay(interval, _timeProvider, waiting.Token).AnyContext();
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Job {JobId} did not finish within {duration}.");
        }
    }

    public Task<bool> RequestCancellationAsync(CancellationToken cancellationToken = default)
    {
        return _requestCancellation(JobId, cancellationToken);
    }
}

/// <summary>
/// Passed to a job on each run. Gives the running job its identity and attempt number, plus store-backed progress
/// reporting, lease heartbeat (for long runs), and cooperative cancellation checks — the parts of
/// <see cref="IJobRuntimeStore"/> that are useful from inside job code. When a job is run outside the durable runtime
/// (for example directly in a test), the store-backed helpers are no-ops and cancellation reflects the supplied token.
/// </summary>
public sealed class JobExecutionContext
{
    private readonly IJobRuntimeStore? _store;
    private readonly string _claimToken;
    private readonly TimeSpan _lease;
    private readonly ReadOnlyMemory<byte>? _payload;
    private readonly string? _payloadType;
    private readonly ISerializer? _serializer;
    private readonly object? _detachedArguments;

    internal JobExecutionContext(string jobId, int attempt, CancellationToken cancellationToken, IJobRuntimeStore store, string claimToken, TimeSpan lease, ReadOnlyMemory<byte>? payload = null, string? payloadType = null, ISerializer? serializer = null)
    {
        JobId = jobId;
        Attempt = attempt;
        CancellationToken = cancellationToken;
        _store = store;
        _claimToken = claimToken;
        _lease = lease;
        _payload = payload;
        _payloadType = payloadType;
        _serializer = serializer;
    }

    /// <summary>
    /// Creates a detached context for running a job outside the durable runtime (tests or one-off invocations).
    /// Progress reporting and lease renewal are no-ops; cancellation reflects <paramref name="cancellationToken"/>;
    /// <paramref name="arguments"/> surfaces through <see cref="GetArguments{TArgs}"/> without serialization.
    /// </summary>
    public JobExecutionContext(CancellationToken cancellationToken = default, string? jobId = null, int attempt = 1, object? arguments = null)
    {
        JobId = jobId ?? Guid.NewGuid().ToString("N");
        Attempt = attempt;
        CancellationToken = cancellationToken;
        _store = null;
        _claimToken = String.Empty;
        _lease = TimeSpan.Zero;
        _detachedArguments = arguments;
    }

    public string JobId { get; }
    public int Attempt { get; }
    public CancellationToken CancellationToken { get; }

    /// <summary>Whether this invocation carries typed arguments (see <see cref="IJobClient.EnqueueAsync{TJob, TArgs}"/>).</summary>
    public bool HasArguments => _detachedArguments is not null || _payload is not null;

    /// <summary>
    /// The typed per-invocation arguments this job was enqueued with. Throws a descriptive
    /// <see cref="InvalidOperationException"/> when the job was enqueued without arguments or the payload cannot be
    /// read as <typeparamref name="TArgs"/> (the stored discriminator is included for triage).
    /// </summary>
    public TArgs GetArguments<TArgs>() where TArgs : class
    {
        if (_detachedArguments is not null)
        {
            return _detachedArguments as TArgs
                ?? throw new InvalidOperationException($"Job \"{JobId}\" arguments are of type \"{_detachedArguments.GetType().FullName}\", not the requested \"{typeof(TArgs).FullName}\".");
        }

        if (_payload is not { } payload)
            throw new InvalidOperationException($"Job \"{JobId}\" was enqueued without arguments. Use EnqueueAsync<TJob, TArgs>(args) to supply a typed payload.");

        // The stored discriminator is a guard, not just forensics: deserializing type A's payload as a structurally
        // similar type B usually SUCCEEDS with silently-wrong data, so a mismatch must fail before deserialization.
        if (_payloadType is not null && !String.Equals(_payloadType, typeof(TArgs).FullName, StringComparison.Ordinal))
            throw new InvalidOperationException($"Job \"{JobId}\" arguments were stored as \"{_payloadType}\" but were requested as \"{typeof(TArgs).FullName}\". Request the type the job was enqueued with.");

        var serializer = _serializer ?? DefaultSerializer.Instance;
        TArgs? args;
        try
        {
            args = serializer.Deserialize(payload, typeof(TArgs)) as TArgs;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Unable to deserialize job \"{JobId}\" arguments (stored type \"{_payloadType}\") as \"{typeof(TArgs).FullName}\".", ex);
        }

        return args ?? throw new InvalidOperationException($"Job \"{JobId}\" arguments (stored type \"{_payloadType}\") deserialized to null as \"{typeof(TArgs).FullName}\".");
    }

    public async Task ReportProgressAsync(int? percent = null, string? message = null, CancellationToken cancellationToken = default)
    {
        CancellationToken.ThrowIfCancellationRequested();
        if (_store is not null && !await _store.ReportJobProgressAsync(JobId, _claimToken, percent, message, cancellationToken == default ? CancellationToken : cancellationToken).AnyContext())
            throw new JobException($"Job {JobId} no longer owns its execution lease.");
    }

    /// <summary>
    /// Forces an immediate lease renewal. Long-running jobs do NOT need to call this — the worker renews the lease
    /// automatically on a supervised loop for the entire run (and cancels the run if the lease is lost). Use it only
    /// to observe lease health explicitly (a false return means another node now owns the job).
    /// </summary>
    public Task<bool> RenewLeaseAsync(CancellationToken cancellationToken = default)
        => _store?.RenewJobLeaseAsync(JobId, _claimToken, _lease, cancellationToken == default ? CancellationToken : cancellationToken) ?? Task.FromResult(true);

    public Task<bool> IsCancellationRequestedAsync(CancellationToken cancellationToken = default)
        => _store?.IsCancellationRequestedAsync(JobId, cancellationToken == default ? CancellationToken : cancellationToken) ?? Task.FromResult(CancellationToken.IsCancellationRequested);
}

public interface IJobMonitor
{
    Task<JobState?> GetAsync(string jobId, CancellationToken cancellationToken = default);
    Task<JobPage> QueryAsync(JobQuery query, CancellationToken cancellationToken = default);
}

public interface IJobClient
{
    Task<JobHandle> EnqueueAsync<TJob>(JobRequestOptions? options = null, CancellationToken cancellationToken = default) where TJob : IJob;

    /// <summary>
    /// Enqueues a job with typed per-invocation arguments. The args are serialized into the durable
    /// <see cref="JobState.Payload"/> via the runtime's serializer and surface to the job through
    /// <see cref="JobExecutionContext.GetArguments{TArgs}"/>.
    /// </summary>
    Task<JobHandle> EnqueueAsync<TJob, TArgs>(TArgs args, JobRequestOptions? options = null, CancellationToken cancellationToken = default) where TJob : IJob<TArgs> where TArgs : class;

    Task<JobHandle> EnqueueAsync(Type jobType, JobRequestOptions? options = null, CancellationToken cancellationToken = default);
    Task<bool> RequestCancellationAsync(string jobId, CancellationToken cancellationToken = default);
}

public interface IJobWorker
{
    /// <summary>False while any execution slot is recovering from an infrastructure failure.</summary>
    bool IsHealthy { get; }
    Task<bool> RunAsync(string jobId, CancellationToken cancellationToken = default);
    Task<int> RunQueuedAsync(int limit = 100, CancellationToken cancellationToken = default);
    /// <summary>Continuously replenishes independent execution slots until shutdown.</summary>
    Task RunContinuouslyAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Durable storage for time-gated dispatches: delayed messages beyond a transport's native ceiling, store-parked
/// retry delays, and delayed publication. This is the only store contract the messaging client depends on —
/// a provider that offers durable scheduling without the full job runtime implements just this.
/// </summary>
public interface IScheduledDispatchStore
{
    Task ScheduleDispatchAsync(ScheduledDispatchState dispatch, CancellationToken cancellationToken = default);
    // Claiming must be atomic per dispatch — in a relational store, a single conditional statement (e.g.
    // SELECT ... FOR UPDATE SKIP LOCKED, or UPDATE ... WHERE due and unclaimed/lease-expired), never read-then-write —
    // so concurrent nodes never claim the same dispatch.
    Task<IReadOnlyList<ScheduledDispatchState>> ClaimDueDispatchesAsync(DateTimeOffset now, int limit, string nodeId, TimeSpan lease, CancellationToken cancellationToken = default);
    Task<bool> CompleteDispatchAsync(string dispatchId, string nodeId, CancellationToken cancellationToken = default);
    Task ReleaseDispatchAsync(string dispatchId, string nodeId, DateTimeOffset nextDueUtc, CancellationToken cancellationToken = default);
}

/// <summary>
/// The full job runtime store: job state persistence, queries, lease/ownership management, cancellation signaling,
/// and scheduled-dispatch storage. The state/lease/cancellation members are deliberately one contract — transitions
/// verify current unexpired claim tokens atomically, so
/// splitting them would break the compare-and-set semantics correctness depends on.
/// </summary>
public interface IJobRuntimeStore : IJobMonitor, IScheduledDispatchStore, IScheduledJobStore
{
    /// <summary>Atomically creates an occurrence, enforcing its unique ID and optional overlap exclusion.</summary>
    Task<JobOccurrenceResult> CreateOccurrenceAsync(JobState initial, bool allowOverlap = false, CancellationToken cancellationToken = default);
    /// <summary>Atomically claims the oldest eligible due job, including recoverable expired executions.</summary>
    Task<JobState?> ClaimNextAsync(JobClaimRequest request, CancellationToken cancellationToken = default);
    /// <summary>Atomically claims a specific eligible job.</summary>
    Task<JobState?> ClaimJobAsync(string jobId, JobClaimRequest request, CancellationToken cancellationToken = default);
    /// <summary>Completes, retries, cancels, or returns work only while the supplied claim is still valid.</summary>
    Task<bool> CompleteJobAsync(string jobId, string claimToken, JobCompletion completion, CancellationToken cancellationToken = default);
    /// <summary>Renews only the current, unexpired execution claim.</summary>
    Task<bool> RenewJobLeaseAsync(string jobId, string claimToken, TimeSpan lease, CancellationToken cancellationToken = default);
    /// <summary>Updates progress only for the current, unexpired execution claim.</summary>
    Task<bool> ReportJobProgressAsync(string jobId, string claimToken, int? percent = null, string? message = null, CancellationToken cancellationToken = default);
    Task<JobRuntimeStoreStats> GetStatsAsync(CancellationToken cancellationToken = default);
    /// <summary>Applies configured history, idempotency and unclaimed occurrence retention in bounded batches.</summary>
    Task<int> CleanupAsync(int limit = 1000, CancellationToken cancellationToken = default);
    Task CreateIfAbsentAsync(JobState initial, CancellationToken cancellationToken = default);
    Task<bool> RequestCancellationAsync(string jobId, CancellationToken cancellationToken = default);
    Task<bool> IsCancellationRequestedAsync(string jobId, CancellationToken cancellationToken = default);
}

public sealed partial class InMemoryJobRuntimeStore : IJobRuntimeStore
{
    private readonly ConcurrentDictionary<string, JobState> _jobs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ScheduledDispatchState> _dispatches = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly object _lock = new();
    private readonly JobRuntimeStoreOptions _options;
    private readonly Dictionary<string, DateTimeOffset> _deduplication = new(StringComparer.Ordinal);
    private readonly PriorityQueue<(string Id, DateTimeOffset Expires), DateTimeOffset> _deduplicationExpiry = new();
    private readonly PriorityQueue<(string Id, DateTimeOffset Completed), DateTimeOffset> _history = new();
    private int _activeJobs;
    private readonly Dictionary<string, JobState> _active = new(StringComparer.Ordinal);

    public InMemoryJobRuntimeStore(TimeProvider? timeProvider = null, int maxJobs = 100000)
        : this(new JobRuntimeStoreOptions { MaxActiveJobs = maxJobs }, timeProvider) { }

    public InMemoryJobRuntimeStore(JobRuntimeStoreOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task CreateIfAbsentAsync(JobState initial, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(initial);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_lock)
        {
            ValidatePayload(initial.Payload?.Length ?? 0);
            initial.RetryPolicy.Validate();
            PurgeDeduplication();
            if (_jobs.ContainsKey(initial.JobId) || _deduplication.ContainsKey(initial.JobId))
                return Task.CompletedTask;
            EnsureCapacity();
            var now = _timeProvider.GetUtcNow();
            StoreJob(initial with
            {
                CreatedUtc = initial.CreatedUtc == default ? now : initial.CreatedUtc,
                LastUpdatedUtc = initial.LastUpdatedUtc == default ? now : initial.LastUpdatedUtc
            });
        }

        return Task.CompletedTask;
    }

    public Task<JobState?> GetAsync(string jobId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _jobs.TryGetValue(jobId, out var state);
        return Task.FromResult(state);
    }

    public Task<JobPage> QueryAsync(JobQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        query.Validate();
        var candidates = _jobs.Values
            .Where(s => (query.Name is null || s.Name == query.Name) && (query.Status is null || s.Status == query.Status))
            .Where(s => query.AfterJobId is null || StringComparer.Ordinal.Compare(s.JobId, query.AfterJobId) > 0)
            .OrderBy(s => s.JobId, StringComparer.Ordinal).Take(query.Limit + 1).ToArray();
        return Task.FromResult(new JobPage(candidates.Take(query.Limit).ToArray(), candidates.Length > query.Limit ? candidates[query.Limit - 1].JobId : null));
    }

    private void EnsureCapacity()
    {
        PurgeDeduplication();
        if (_activeJobs >= _options.MaxActiveJobs)
            throw CapacityExceeded("active jobs", _options.MaxActiveJobs);
        if (_deduplication.Count >= _options.MaxDeduplicationRecords)
            throw CapacityExceeded("idempotency records", _options.MaxDeduplicationRecords);
    }

    private static JobException CapacityExceeded(string budget, int maximum)
    {
        JobInstruments.CapacityRejected.Add(1, new KeyValuePair<string, object?>("budget", budget));
        return new JobException($"Job store {budget} capacity ({maximum}) reached. Configure JobRuntimeStoreOptions to increase this budget.");
    }

    private void ValidatePayload(long bytes)
    {
        if (bytes > _options.MaxPayloadBytes)
            throw new JobException($"Payload exceeds the configured {_options.MaxPayloadBytes} byte limit.");
    }

    private static bool IsActive(JobState state) => state.Status is JobStatus.Queued or JobStatus.Scheduled or JobStatus.Processing;

    private void StoreJob(JobState state)
    {
        _jobs.TryGetValue(state.JobId, out var previous);
        bool active = IsActive(state);
        _activeJobs += (active ? 1 : 0) - (previous is not null && IsActive(previous) ? 1 : 0);
        if (!active && state.CompletedUtc is null)
            state = state with { CompletedUtc = _timeProvider.GetUtcNow() };
        _jobs[state.JobId] = state;
        if (active) _active[state.JobId] = state;
        else _active.Remove(state.JobId);
        if (previous is null)
            _deduplication[state.JobId] = DateTimeOffset.MaxValue;
        if (!active && (previous is null || previous.CompletedUtc != state.CompletedUtc || IsActive(previous)))
        {
            var completed = state.CompletedUtc!.Value;
            var expires = completed.Add(_options.DeduplicationRetention);
            _deduplication[state.JobId] = expires;
            _deduplicationExpiry.Enqueue((state.JobId, expires), expires);
            _history.Enqueue((state.JobId, completed), completed);
            TrimHistory(1000, pressureOnly: true);
        }
    }

    private void PurgeDeduplication()
    {
        var now = _timeProvider.GetUtcNow();
        while (_deduplicationExpiry.TryPeek(out var item, out var expires) && expires <= now)
        {
            _deduplicationExpiry.Dequeue();
            if (_deduplication.TryGetValue(item.Id, out var current) && current == item.Expires)
                _deduplication.Remove(item.Id);
        }
    }

    private int TrimHistory(int limit, bool pressureOnly = false)
    {
        int removed = 0;
        var cutoff = _timeProvider.GetUtcNow().Subtract(_options.HistoryRetention);
        while (removed < limit && _history.TryPeek(out var item, out var completed))
        {
            if (_jobs.Count - _activeJobs <= _options.MaxHistoryJobs && (pressureOnly || completed > cutoff))
                break;
            _history.Dequeue();
            if (_jobs.TryGetValue(item.Id, out var state) && !IsActive(state) && state.CompletedUtc == item.Completed && _jobs.TryRemove(item.Id, out _))
                removed++;
        }
        return removed;
    }

    public Task<JobRuntimeStoreStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
            return Task.FromResult(new JobRuntimeStoreStats(_activeJobs, _jobs.Count - _activeJobs, _deduplication.Count, _dispatches.Count));
    }

    public Task<int> CleanupAsync(int limit = 1000, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 1000);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            PurgeDeduplication();
            var now = _timeProvider.GetUtcNow();
            foreach (var state in _jobs.Values.Where(s => s.RequiredNodeId is not null && s.Attempt == 0 && s.Status is JobStatus.Queued or JobStatus.Scheduled && s.ExpiresUtc <= now).Take(limit))
                StoreJob(state with { Status = JobStatus.Cancelled, CompletedUtc = now, LastUpdatedUtc = now, ResultMessage = "Unclaimed per-node occurrence expired." });
            return Task.FromResult(TrimHistory(limit));
        }
    }

    public Task<bool> RequestCancellationAsync(string jobId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(UpdateJob(jobId, state => state with
        {
            CancellationRequested = true,
            Status = state.Status is JobStatus.Queued or JobStatus.Scheduled ? JobStatus.Cancelled : state.Status,
            CompletedUtc = state.Status is JobStatus.Queued or JobStatus.Scheduled ? _timeProvider.GetUtcNow() : state.CompletedUtc,
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
        lock (_lock)
        {
            if (_dispatches.ContainsKey(dispatch.DispatchId))
                return Task.CompletedTask;
            ValidatePayload(dispatch.Body.Length + dispatch.Headers.Sum(h => (long)System.Text.Encoding.UTF8.GetByteCount(h.Key) + System.Text.Encoding.UTF8.GetByteCount(h.Value)));
            if (_dispatches.Count >= _options.MaxScheduledDispatches)
                throw CapacityExceeded("scheduled dispatches", _options.MaxScheduledDispatches);
            _dispatches.TryAdd(dispatch.DispatchId, dispatch with { Body = dispatch.Body.ToArray() });
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ScheduledDispatchState>> ClaimDueDispatchesAsync(DateTimeOffset now, int limit, string nodeId, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrEmpty(nodeId);

        lock (_lock)
        {
            var due = _dispatches.Values
                .Where(d => d.DueUtc <= now && (String.IsNullOrEmpty(d.ClaimOwner) || d.ClaimExpiresUtc <= _timeProvider.GetUtcNow()))
                .OrderBy(d => d.DueUtc)
                .Take(Math.Max(1, limit))
                .ToArray();

            for (int index = 0; index < due.Length; index++)
            {
                var claimed = due[index] with
                {
                    ClaimOwner = nodeId,
                    ClaimExpiresUtc = _timeProvider.GetUtcNow().Add(lease),
                    Attempts = due[index].Attempts + 1
                };
                _dispatches[claimed.DispatchId] = claimed;
                due[index] = claimed;
            }

            return Task.FromResult<IReadOnlyList<ScheduledDispatchState>>(due);
        }
    }

    public Task<bool> CompleteDispatchAsync(string dispatchId, string nodeId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_lock)
        {
            if (_dispatches.TryGetValue(dispatchId, out var dispatch) && dispatch.ClaimOwner == nodeId && dispatch.ClaimExpiresUtc > _timeProvider.GetUtcNow())
                return Task.FromResult(_dispatches.TryRemove(dispatchId, out _));
            return Task.FromResult(false);
        }
    }

    public Task ReleaseDispatchAsync(string dispatchId, string nodeId, DateTimeOffset nextDueUtc, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_lock)
        {
            if (_dispatches.TryGetValue(dispatchId, out var dispatch) && dispatch.ClaimOwner == nodeId && dispatch.ClaimExpiresUtc > _timeProvider.GetUtcNow())
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

            StoreJob(update(current));
            return true;
        }
    }


}

public sealed class JobClient : IJobClient
{
    private readonly IJobRuntimeStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly IJobTypeRegistry _jobTypes;
    private readonly bool _requireRegisteredTypes;
    private readonly ISerializer _serializer;

    public JobClient(IJobRuntimeStore store, TimeProvider? timeProvider = null, IJobTypeRegistry? jobTypes = null, ISerializer? serializer = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _jobTypes = jobTypes ?? new JobTypeRegistry();
        _requireRegisteredTypes = jobTypes is not null;
        _serializer = serializer ?? DefaultSerializer.Instance;
    }

    public Task<JobHandle> EnqueueAsync<TJob>(JobRequestOptions? options = null, CancellationToken cancellationToken = default) where TJob : IJob
    {
        return EnqueueCoreAsync(typeof(TJob), args: null, options, cancellationToken);
    }

    public Task<JobHandle> EnqueueAsync<TJob, TArgs>(TArgs args, JobRequestOptions? options = null, CancellationToken cancellationToken = default) where TJob : IJob<TArgs> where TArgs : class
    {
        ArgumentNullException.ThrowIfNull(args);
        return EnqueueCoreAsync(typeof(TJob), args, options, cancellationToken);
    }

    public Task<JobHandle> EnqueueAsync(Type jobType, JobRequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        return EnqueueCoreAsync(jobType, args: null, options, cancellationToken);
    }

    private async Task<JobHandle> EnqueueCoreAsync(Type jobType, object? args, JobRequestOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jobType);
        if (!typeof(IJob).IsAssignableFrom(jobType))
            throw new ArgumentException("Job type must implement IJob.", nameof(jobType));

        JobArgumentContract.Validate(jobType, args);
        options ??= new JobRequestOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxAttempts, 1);
        options.RetryPolicy.Validate();
        if (options.Delay is not null && options.RunAt is not null)
            throw new ArgumentException("Specify either Delay or RunAt, not both.", nameof(options));
        if (options.Delay is { } delay)
            ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
        string jobId = options.JobId ?? Guid.NewGuid().ToString("N");
        string name = options.Name ?? jobType.Name;
        var now = _timeProvider.GetUtcNow();

        string typeName = _jobTypes.GetName(jobType);
        if (_requireRegisteredTypes) _jobTypes.Resolve(typeName);
        await _store.CreateIfAbsentAsync(new JobState
        {
            JobId = jobId,
            Name = name,
            JobType = typeName,
            MaxAttempts = options.MaxAttempts,
            RetryPolicy = options.RetryPolicy,
            AvailableUtc = options.RunAt ?? (options.Delay is { } delayValue ? now.Add(delayValue) : now),
            // Explicitly typed: the byte[] -> ReadOnlyMemory conversion maps a null array to an EMPTY memory, which
            // would make an argless job look like it carries a zero-byte payload.
            Payload = args is null ? null : (ReadOnlyMemory<byte>?)_serializer.SerializeToBytes(args),
            PayloadType = args?.GetType().FullName,
            Status = JobStatus.Queued,
            CreatedUtc = now,
            LastUpdatedUtc = now
        }, cancellationToken).ConfigureAwait(false);

        return new JobHandle(jobId, _store, RequestCancellationAsync, _timeProvider);
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
    public static string? Configured => Environment.GetEnvironmentVariable("FOUNDATIO_NODE_ID") is { } value && !String.IsNullOrWhiteSpace(value) ? value : null;
    public static string RequireStable(string? nodeId)
        => !String.IsNullOrWhiteSpace(nodeId) ? nodeId : Configured
            ?? throw new JobException("PerNode schedules require a stable NodeId. Configure Jobs.ConfigureWorker(o => o with { NodeId = ... }) or FOUNDATIO_NODE_ID; use Global for fleet-wide work.");
    public static string Current { get; } = Resolve();

    private static string Resolve()
    {
        string? configured = Environment.GetEnvironmentVariable("FOUNDATIO_NODE_ID");
        if (!String.IsNullOrEmpty(configured))
            return configured;

        return $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid().ToString("N")[..8]}";
    }
}

/// <summary>
/// Optional dependencies and tuning for <see cref="JobWorker"/>. Prefer the options-taking constructor when
/// hand-wiring a worker; unset properties fall back to the same defaults as the full constructor.
/// </summary>
public sealed record JobWorkerOptions
{
    public Microsoft.Extensions.Logging.ILoggerFactory? LoggerFactory { get; init; }
    public TimeProvider? TimeProvider { get; init; }
    public string? NodeId { get; init; }
    public TimeSpan? Lease { get; init; }
    public IJobTypeRegistry? JobTypes { get; init; }
    public TimeSpan? CancellationPollInterval { get; init; }
    public ISerializer? Serializer { get; init; }
    public int MaxConcurrency { get; init; } = 1;
}
