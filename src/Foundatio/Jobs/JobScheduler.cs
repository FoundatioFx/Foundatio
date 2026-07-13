using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Cronos;
using Foundatio.Serializer;
using Foundatio.Messaging;

namespace Foundatio.Jobs;

public enum ScheduledJobScope
{
    Global,
    PerNode
}

public enum OverlapPolicy
{
    SkipIfRunning,
    AllowConcurrent
}

public sealed record ScheduledJobDefinition
{
    /// <summary>
    /// The schedule name a job type gets when none is given explicitly (the type's simple name). This is the single
    /// home of the convention shared by <c>AddCronJob&lt;TJob&gt;</c> and the generic
    /// <see cref="IScheduledJobManager"/> overloads, so type-addressed management always finds type-registered schedules.
    /// </summary>
    public static string DefaultNameFor(Type jobType)
    {
        ArgumentNullException.ThrowIfNull(jobType);
        return jobType.Name;
    }

    public required string Name { get; init; }
    public required string Cron { get; init; }
    public Type? JobType { get; init; }
    public TimeZoneInfo? TimeZone { get; init; }
    public ScheduledJobScope Scope { get; init; } = ScheduledJobScope.Global;
    public OverlapPolicy Overlap { get; init; } = OverlapPolicy.SkipIfRunning;
    public TimeSpan? MisfireWindow { get; init; }
    /// <summary>Maximum TOTAL run attempts for a failed occurrence before it is dead-lettered (same semantics as the
    /// messaging RetryPolicy and pump MaxJobAttempts). Default 3.</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>
    /// Computes the delay before a failed occurrence is retried, given the attempt number (1-based).
    /// Defaults to capped exponential backoff when null.
    /// </summary>
    public Func<int, TimeSpan>? RetryBackoff { get; init; }

    /// <summary>
    /// Typed arguments serialized into every occurrence's <see cref="JobState.Payload"/>; the job reads them via
    /// <see cref="JobExecutionContext.GetArguments{TArgs}"/>. Null when the job takes none.
    /// </summary>
    public object? Arguments { get; init; }

    public bool Enabled { get; init; } = true;
}

/// <summary>
/// Options for a declaratively-registered CRON job — <c>AddFoundatio().Jobs.AddCronJob&lt;TJob&gt;(cron, o =&gt; ...)</c>.
/// The registered definitions are scheduled automatically when the runtime pump starts.
/// </summary>
public sealed class CronJobOptions
{
    /// <summary>Schedule name (must be unique across scheduled jobs). Defaults to the job type name.</summary>
    public string? Name { get; set; }

    /// <summary>Global (one instance per tick, the default) or PerNode (every instance runs it per tick).</summary>
    public ScheduledJobScope Scope { get; set; } = ScheduledJobScope.Global;

    /// <summary>Whether a new occurrence is skipped while a prior one is still running. Default SkipIfRunning.</summary>
    public OverlapPolicy Overlap { get; set; } = OverlapPolicy.SkipIfRunning;

    /// <summary>How late a missed occurrence may still fire. Null uses the scheduler default.</summary>
    public TimeSpan? MisfireWindow { get; set; }

    /// <summary>Maximum TOTAL run attempts for a failed occurrence before dead-lettering. Default 3.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Whether the schedule is active. Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Time zone the CRON expression is evaluated in. Null uses the scheduler default (UTC).</summary>
    public TimeZoneInfo? TimeZone { get; set; }

    /// <summary>Typed arguments serialized into every occurrence's payload (see <see cref="ScheduledJobDefinition.Arguments"/>).</summary>
    public object? Arguments { get; set; }
}

/// <summary>
/// Storage contract for scheduled (CRON) job definitions. Implementations persist the definitions themselves;
/// <see cref="IScheduledJobManager"/> is the user-facing management API layered on top of this store.
/// </summary>
public interface IScheduledJobStore
{
    Task ScheduleAsync(ScheduledJobDefinition definition, CancellationToken cancellationToken = default);
    Task UnscheduleAsync(string name, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ScheduledJobDefinition>> GetSchedulesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Runtime management surface for scheduled (CRON) jobs: list and inspect schedules, add or replace definitions,
/// change a schedule's cron expression, enable/disable, and trigger an immediate occurrence. Declaratively-registered
/// jobs (<c>AddCronJob&lt;TJob&gt;</c>) and definitions added here share the same <see cref="IScheduledJobStore"/> store,
/// so both are manageable through this interface.
/// </summary>
public interface IScheduledJobManager
{
    Task<IReadOnlyList<ScheduledJobDefinition>> GetSchedulesAsync(CancellationToken cancellationToken = default);
    Task<ScheduledJobDefinition?> GetScheduleAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Adds a new schedule or replaces the existing definition with the same name.</summary>
    Task ScheduleAsync(ScheduledJobDefinition definition, CancellationToken cancellationToken = default);

    Task UnscheduleAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Changes an existing schedule's cron expression (validated). Returns false when no schedule has that name.</summary>
    Task<bool> RescheduleAsync(string name, string cronSchedule, CancellationToken cancellationToken = default);

    /// <summary>
    /// Enables or disables a schedule. A disabled schedule materializes no occurrences (and cannot be triggered)
    /// until re-enabled. Returns false when no schedule has that name.
    /// </summary>
    Task<bool> SetEnabledAsync(string name, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>
    /// Triggers an immediate occurrence of the named schedule, independent of its cron expression, and returns a
    /// <see cref="JobHandle"/> for watching or cancelling the run. The occurrence is durable (materialized into the
    /// runtime store and executed by the pump) and uses the definition's retry/dead-letter budget and
    /// <see cref="ScheduledJobDefinition.Arguments"/>. Manual occurrences run regardless of
    /// <see cref="ScheduledJobDefinition.Overlap"/> and are not counted by SkipIfRunning accounting — the trigger is a
    /// deliberate operator action. Throws when the schedule does not exist, is disabled, or has no job type.
    /// </summary>
    Task<JobHandle> TriggerAsync(string name, CancellationToken cancellationToken = default);
}

/// <summary>
/// Type-addressed conveniences over <see cref="IScheduledJobManager"/>: they resolve the schedule name from the job
/// type via <see cref="ScheduledJobDefinition.DefaultNameFor"/> — the same default <c>AddCronJob&lt;TJob&gt;</c> uses —
/// so a schedule registered without an explicit name is manageable by its type alone. Schedules registered under a
/// custom <see cref="ScheduledJobDefinition.Name"/> are addressed with the string overloads.
/// </summary>
public static class ScheduledJobManagerExtensions
{
    public static Task<ScheduledJobDefinition?> GetScheduleAsync<TJob>(this IScheduledJobManager manager, CancellationToken cancellationToken = default) where TJob : IJob
        => Manager(manager).GetScheduleAsync(ScheduledJobDefinition.DefaultNameFor(typeof(TJob)), cancellationToken);

    public static Task<JobHandle> TriggerAsync<TJob>(this IScheduledJobManager manager, CancellationToken cancellationToken = default) where TJob : IJob
        => Manager(manager).TriggerAsync(ScheduledJobDefinition.DefaultNameFor(typeof(TJob)), cancellationToken);

    public static Task<bool> RescheduleAsync<TJob>(this IScheduledJobManager manager, string cronSchedule, CancellationToken cancellationToken = default) where TJob : IJob
        => Manager(manager).RescheduleAsync(ScheduledJobDefinition.DefaultNameFor(typeof(TJob)), cronSchedule, cancellationToken);

    public static Task<bool> SetEnabledAsync<TJob>(this IScheduledJobManager manager, bool enabled, CancellationToken cancellationToken = default) where TJob : IJob
        => Manager(manager).SetEnabledAsync(ScheduledJobDefinition.DefaultNameFor(typeof(TJob)), enabled, cancellationToken);

    public static Task UnscheduleAsync<TJob>(this IScheduledJobManager manager, CancellationToken cancellationToken = default) where TJob : IJob
        => Manager(manager).UnscheduleAsync(ScheduledJobDefinition.DefaultNameFor(typeof(TJob)), cancellationToken);

    private static IScheduledJobManager Manager(IScheduledJobManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        return manager;
    }
}

public sealed class ScheduledJobManager : IScheduledJobManager
{
    private readonly IScheduledJobStore _scheduleStore;
    private readonly IJobRuntimeStore _store;
    private readonly IJobTypeRegistry _jobTypes;
    private readonly ISerializer _serializer;
    private readonly TimeProvider _timeProvider;

    public ScheduledJobManager(IScheduledJobStore scheduleStore, IJobRuntimeStore store, IJobTypeRegistry? jobTypes = null, ISerializer? serializer = null, TimeProvider? timeProvider = null)
    {
        _scheduleStore = scheduleStore ?? throw new ArgumentNullException(nameof(scheduleStore));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _jobTypes = jobTypes ?? new JobTypeRegistry();
        _serializer = serializer ?? DefaultSerializer.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<IReadOnlyList<ScheduledJobDefinition>> GetSchedulesAsync(CancellationToken cancellationToken = default)
        => _scheduleStore.GetSchedulesAsync(cancellationToken);

    public async Task<ScheduledJobDefinition?> GetScheduleAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var schedules = await _scheduleStore.GetSchedulesAsync(cancellationToken).ConfigureAwait(false);
        return schedules.FirstOrDefault(s => String.Equals(s.Name, name, StringComparison.Ordinal));
    }

    public Task ScheduleAsync(ScheduledJobDefinition definition, CancellationToken cancellationToken = default)
        => _scheduleStore.ScheduleAsync(definition, cancellationToken);

    public Task UnscheduleAsync(string name, CancellationToken cancellationToken = default)
        => _scheduleStore.UnscheduleAsync(name, cancellationToken);

    public async Task<bool> RescheduleAsync(string name, string cronSchedule, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(cronSchedule);
        JobScheduleProcessor.ValidateCron(cronSchedule);

        var definition = await GetScheduleAsync(name, cancellationToken).ConfigureAwait(false);
        if (definition is null)
            return false;

        await _scheduleStore.ScheduleAsync(definition with { Cron = cronSchedule }, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> SetEnabledAsync(string name, bool enabled, CancellationToken cancellationToken = default)
    {
        var definition = await GetScheduleAsync(name, cancellationToken).ConfigureAwait(false);
        if (definition is null)
            return false;

        if (definition.Enabled != enabled)
            await _scheduleStore.ScheduleAsync(definition with { Enabled = enabled }, cancellationToken).ConfigureAwait(false);

        return true;
    }

    public async Task<JobHandle> TriggerAsync(string name, CancellationToken cancellationToken = default)
    {
        var definition = await GetScheduleAsync(name, cancellationToken).ConfigureAwait(false)
            ?? throw new ScheduledJobNotFoundException(name);

        if (definition.JobType is null)
            throw new JobException($"Scheduled job \"{name}\" has no job type and cannot be triggered.");

        // The occurrence-run path releases (and endlessly re-claims) dispatches whose definition is disabled, so a
        // trigger of a disabled schedule would park forever rather than run — refuse it up front instead.
        if (!definition.Enabled)
            throw new ScheduledJobDisabledException(name);

        var now = _timeProvider.GetUtcNow();

        // Unique id: manual runs are deliberate, so they never dedupe against each other or against cron occurrences
        // (whose deterministic "{name}:{timestamp}:{scope}" ids exist precisely to dedupe scheduler ticks).
        string jobId = $"{name}:manual:{Guid.NewGuid():N}";

        await _store.CreateIfAbsentAsync(new JobState
        {
            JobId = jobId,
            Name = definition.Name,
            JobType = _jobTypes.GetName(definition.JobType),
            Payload = definition.Arguments is null ? null : (ReadOnlyMemory<byte>?)_serializer.SerializeToBytes(definition.Arguments),
            PayloadType = definition.Arguments?.GetType().FullName,
            Status = JobStatus.Scheduled,
            CreatedUtc = now,
            LastUpdatedUtc = now,
            ScheduledForUtc = now
        }, cancellationToken).ConfigureAwait(false);

        await _store.ScheduleDispatchAsync(new ScheduledDispatchState
        {
            DispatchId = jobId,
            Kind = ScheduledDispatchKind.JobOccurrence,
            JobName = definition.Name,
            Body = Array.Empty<byte>(),
            Headers = MessageHeaders.Create([
                new KeyValuePair<string, string>("job.name", definition.Name),
                new KeyValuePair<string, string>("job.scheduled_for", now.UtcDateTime.ToString("O")),
                new KeyValuePair<string, string>("job.trigger", "manual")
            ]),
            DueUtc = now,
            JobId = jobId
        }, cancellationToken).ConfigureAwait(false);

        return new JobHandle(jobId, _store, _store.RequestCancellationAsync);
    }
}

public sealed class InMemoryScheduledJobStore : IScheduledJobStore
{
    private readonly ConcurrentDictionary<string, ScheduledJobDefinition> _definitions = new(StringComparer.Ordinal);

    public Task ScheduleAsync(ScheduledJobDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrEmpty(definition.Name);
        ArgumentException.ThrowIfNullOrEmpty(definition.Cron);
        cancellationToken.ThrowIfCancellationRequested();

        if (definition.MaxAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(definition), definition.MaxAttempts, "MaxAttempts must be at least 1 (it is the TOTAL number of run attempts).");

        if (definition.JobType is not null && !typeof(IJob).IsAssignableFrom(definition.JobType))
            throw new ArgumentException("JobType must implement IJob.", nameof(definition));

        JobScheduleProcessor.ValidateCron(definition.Cron);
        _definitions[definition.Name] = definition;
        return Task.CompletedTask;
    }

    public Task UnscheduleAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        cancellationToken.ThrowIfCancellationRequested();
        _definitions.TryRemove(name, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ScheduledJobDefinition>> GetSchedulesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<ScheduledJobDefinition>>(_definitions.Values.OrderBy(d => d.Name, StringComparer.Ordinal).ToArray());
    }
}

/// <summary>
/// Optional dependencies for <see cref="JobScheduleProcessor"/>. Prefer the options-taking constructor when
/// hand-wiring a processor; unset properties fall back to the same defaults as the full constructor.
/// </summary>
public sealed record JobScheduleProcessorOptions
{
    public TimeProvider? TimeProvider { get; init; }
    public string? NodeId { get; init; }
    public IMessageTransport? Transport { get; init; }
    public IJobTypeRegistry? JobTypes { get; init; }
    public ISerializer? Serializer { get; init; }
}

public sealed class JobScheduleProcessor
{
    private static readonly TimeSpan DefaultLease = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultMisfireWindow = TimeSpan.FromMinutes(1);

    private readonly IScheduledJobStore _scheduleStore;
    private readonly IJobRuntimeStore _store;
    private readonly IJobWorker _jobWorker;
    private readonly TimeProvider _timeProvider;
    private readonly IJobTypeRegistry _jobTypes;
    private readonly ISerializer _serializer;
    private readonly string _nodeId;
    private readonly IMessageTransport? _transport;

    /// <summary>Preferred overload for hand-wiring: the optional dependencies come in as one options record.</summary>
    public JobScheduleProcessor(IScheduledJobStore scheduleStore, IJobRuntimeStore store, IJobWorker jobWorker, JobScheduleProcessorOptions? options = null)
        : this(scheduleStore, store, jobWorker, options?.TimeProvider, options?.NodeId, options?.Transport, options?.JobTypes, options?.Serializer)
    {
    }

    public JobScheduleProcessor(IScheduledJobStore scheduleStore, IJobRuntimeStore store, IJobWorker jobWorker, TimeProvider? timeProvider = null, string? nodeId = null, IMessageTransport? transport = null, IJobTypeRegistry? jobTypes = null, ISerializer? serializer = null)
    {
        _scheduleStore = scheduleStore ?? throw new ArgumentNullException(nameof(scheduleStore));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _jobWorker = jobWorker ?? throw new ArgumentNullException(nameof(jobWorker));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _jobTypes = jobTypes ?? new JobTypeRegistry();
        _serializer = serializer ?? DefaultSerializer.Instance;
        _nodeId = !String.IsNullOrEmpty(nodeId) ? nodeId : NodeIdentity.Current;
        _transport = transport;
    }

    public Task<IReadOnlyList<ScheduledDispatchState>> EnqueueDueOccurrencesAsync(CancellationToken cancellationToken = default)
    {
        return EnqueueDueOccurrencesAsync(_timeProvider.GetUtcNow(), cancellationToken);
    }

    public async Task<IReadOnlyList<ScheduledDispatchState>> EnqueueDueOccurrencesAsync(DateTimeOffset utcNow, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var scheduled = new List<ScheduledDispatchState>();
        var definitions = await _scheduleStore.GetSchedulesAsync(cancellationToken).ConfigureAwait(false);

        foreach (var definition in definitions)
        {
            if (!definition.Enabled)
                continue;

            var cron = ParseCron(definition.Cron);
            var timeZone = definition.TimeZone ?? TimeZoneInfo.Utc;
            var window = definition.MisfireWindow ?? DefaultMisfireWindow;
            if (window < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(definition), window, "MisfireWindow must be greater than or equal to zero.");

            string scopeKey = GetScopeKey(definition);

            // Materialize every occurrence that fell due within the misfire window, not just the most recent, so a
            // scheduler that lagged behind the cadence does not silently drop intermediate ticks. Deterministic
            // occurrence ids dedupe across overlapping windows and across nodes ticking simultaneously.
            var occurrences = cron.GetOccurrences(utcNow - window, utcNow, timeZone, fromInclusive: true, toInclusive: true).ToList();
            if (occurrences.Count == 0)
                continue;

            if (definition.Overlap == OverlapPolicy.SkipIfRunning)
            {
                // Don't stampede: if a prior occurrence is still pending or running, skip this tick entirely;
                // otherwise collapse the window to a single (most recent) catch-up occurrence.
                if (await HasActiveOccurrenceAsync(definition.Name, scopeKey, cancellationToken).ConfigureAwait(false))
                    continue;

                occurrences = [occurrences[^1]];
            }

            foreach (var occurrence in occurrences)
            {
                string jobId = CreateOccurrenceId(definition.Name, occurrence, scopeKey);

                if (await _store.GetAsync(jobId, cancellationToken).ConfigureAwait(false) is not null)
                    continue;

                await _store.CreateIfAbsentAsync(new JobState
                {
                    JobId = jobId,
                    Name = definition.Name,
                    JobType = GetJobTypeName(definition.JobType),
                    // Explicitly typed: the byte[] -> ReadOnlyMemory conversion maps a null array to an EMPTY memory,
                    // which would make an argless occurrence look like it carries a zero-byte payload.
                    Payload = definition.Arguments is null ? null : (ReadOnlyMemory<byte>?)_serializer.SerializeToBytes(definition.Arguments),
                    PayloadType = definition.Arguments?.GetType().FullName,
                    Status = JobStatus.Scheduled,
                    CreatedUtc = utcNow,
                    LastUpdatedUtc = utcNow,
                    ScheduledForUtc = occurrence
                }, cancellationToken).ConfigureAwait(false);

                var dispatch = new ScheduledDispatchState
                {
                    DispatchId = jobId,
                    Kind = ScheduledDispatchKind.JobOccurrence,
                    JobName = definition.Name,
                    Body = Array.Empty<byte>(),
                    Headers = CreateOccurrenceHeaders(definition, occurrence, scopeKey),
                    DueUtc = utcNow,
                    JobId = jobId
                };

                await _store.ScheduleDispatchAsync(dispatch, cancellationToken).ConfigureAwait(false);
                scheduled.Add(dispatch);
            }
        }

        return scheduled;
    }

    public Task<int> RunDueOccurrencesAsync(CancellationToken cancellationToken = default)
    {
        return RunDueOccurrencesAsync(_timeProvider.GetUtcNow(), 100, null, cancellationToken);
    }

    public async Task<int> RunDueOccurrencesAsync(DateTimeOffset utcNow, int limit = 100, TimeSpan? lease = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var definitions = (await _scheduleStore.GetSchedulesAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(d => d.Name, StringComparer.Ordinal);

        var dispatches = await _store.ClaimDueDispatchesAsync(utcNow, limit, _nodeId, lease ?? DefaultLease, cancellationToken).ConfigureAwait(false);
        int completed = 0;

        // Materialize delayed/scheduled MESSAGES before running any job occurrence: message dispatch is cheap and
        // latency-sensitive (it is the messaging delayed-delivery fallback), so it must never wait behind a long job
        // run that happened to be claimed earlier in the same batch.
        foreach (var dispatch in dispatches)
        {
            if (dispatch.Kind is ScheduledDispatchKind.QueueMessage or ScheduledDispatchKind.PubSubMessage)
            {
                await MaterializeMessageDispatchAsync(dispatch, cancellationToken).ConfigureAwait(false);
                completed++;
            }
        }

        foreach (var dispatch in dispatches)
        {
            if (dispatch.Kind is ScheduledDispatchKind.QueueMessage or ScheduledDispatchKind.PubSubMessage)
                continue;

            if (dispatch.Kind != ScheduledDispatchKind.JobOccurrence)
            {
                await _store.ReleaseDispatchAsync(dispatch.DispatchId, _nodeId, utcNow.AddMinutes(1), cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (dispatch.JobName is null || !definitions.TryGetValue(dispatch.JobName, out var definition) || !definition.Enabled || definition.JobType is null)
            {
                await _store.ReleaseDispatchAsync(dispatch.DispatchId, _nodeId, utcNow.AddMinutes(1), cancellationToken).ConfigureAwait(false);
                continue;
            }

            string jobId = dispatch.JobId ?? dispatch.DispatchId;

            try
            {
                if (!await TryPrepareOccurrenceForRunAsync(jobId, definition, utcNow, cancellationToken).ConfigureAwait(false))
                {
                    // Retire (don't reschedule) the dispatch when the occurrence has reached a terminal state — e.g. it
                    // was dead-lettered in TryPrepareOccurrenceForRunAsync, or a worker completed it but crashed before
                    // CompleteDispatchAsync. Otherwise a terminal occurrence's dispatch would be re-claimed forever.
                    var pending = await _store.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
                    if (pending is { Status: JobStatus.Completed or JobStatus.Cancelled or JobStatus.DeadLettered })
                        await _store.CompleteDispatchAsync(dispatch.DispatchId, _nodeId, cancellationToken).ConfigureAwait(false);
                    else
                        await _store.ReleaseDispatchAsync(dispatch.DispatchId, _nodeId, utcNow.AddMinutes(1), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await _jobWorker.RunAsync(jobId, cancellationToken).ConfigureAwait(false);

                var state = await _store.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
                if (state?.Status == JobStatus.Failed)
                {
                    if (state.Attempt < definition.MaxAttempts)
                    {
                        await _store.TryTransitionAsync(jobId, JobStatus.Failed, JobStatus.Scheduled, new JobStatePatch
                        {
                            ClearNodeId = true,
                            ClearLeaseExpiresUtc = true,
                            LastUpdatedUtc = utcNow
                        }, cancellationToken: cancellationToken).ConfigureAwait(false);
                        await _store.ReleaseDispatchAsync(dispatch.DispatchId, _nodeId, utcNow.Add(GetRetryBackoff(definition, state.Attempt)), cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    await _store.TryTransitionAsync(jobId, JobStatus.Failed, JobStatus.DeadLettered, new JobStatePatch
                    {
                        ClearNodeId = true,
                        ClearLeaseExpiresUtc = true,
                        LastUpdatedUtc = utcNow
                    }, cancellationToken: cancellationToken).ConfigureAwait(false);
                }

                await _store.CompleteDispatchAsync(dispatch.DispatchId, _nodeId, cancellationToken).ConfigureAwait(false);
                completed++;
            }
            catch
            {
                await _store.ReleaseDispatchAsync(dispatch.DispatchId, _nodeId, utcNow.AddMinutes(1), CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }

        return completed;
    }

    private async Task MaterializeMessageDispatchAsync(ScheduledDispatchState dispatch, CancellationToken cancellationToken)
    {
        if (_transport is null)
            throw new InvalidOperationException("A message transport is required to materialize scheduled queue and pub/sub dispatches.");

        if (dispatch.Destination is null)
            throw new InvalidOperationException($"Scheduled {dispatch.Kind} dispatch \"{dispatch.DispatchId}\" has no destination address.");

        await _transport.SendAsync(dispatch.Destination, [
            new TransportMessage
            {
                MessageId = dispatch.DispatchId,
                Body = dispatch.Body,
                Headers = dispatch.Headers
            }
        ], dispatch.Options with { DeliverAt = null }, cancellationToken).ConfigureAwait(false);

        // SendAsync is throw-on-failure; reaching here means the dispatch was materialized, so retire it.
        await _store.CompleteDispatchAsync(dispatch.DispatchId, _nodeId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> TryPrepareOccurrenceForRunAsync(string jobId, ScheduledJobDefinition definition, DateTimeOffset utcNow, CancellationToken cancellationToken)
    {
        if (await _store.TryTransitionAsync(jobId, JobStatus.Scheduled, JobStatus.Queued, new JobStatePatch { JobType = GetJobTypeName(definition.JobType), LastUpdatedUtc = utcNow }, cancellationToken: cancellationToken).ConfigureAwait(false))
            return true;

        var state = await _store.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (state?.Status != JobStatus.Processing || state.LeaseExpiresUtc is null || state.LeaseExpiresUtc > utcNow)
            return false;

        if (state.Attempt >= definition.MaxAttempts)
        {
            await _store.TryTransitionAsync(jobId, JobStatus.Processing, JobStatus.DeadLettered, new JobStatePatch
            {
                ClearNodeId = true,
                ClearLeaseExpiresUtc = true,
                LastUpdatedUtc = utcNow
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
            return false;
        }

        return await _store.TryTransitionAsync(jobId, JobStatus.Processing, JobStatus.Queued, new JobStatePatch
        {
            JobType = GetJobTypeName(definition.JobType),
            ClearNodeId = true,
            ClearLeaseExpiresUtc = true,
            LastUpdatedUtc = utcNow
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private string? GetJobTypeName(Type? jobType)
    {
        return jobType is null ? null : _jobTypes.GetName(jobType);
    }

    private static TimeSpan GetRetryBackoff(ScheduledJobDefinition definition, int attempt)
    {
        if (definition.RetryBackoff is { } custom)
            return custom(attempt);

        // Capped exponential backoff: 1s, 2s, 4s, ... up to 5 minutes.
        double seconds = Math.Min(300, Math.Pow(2, Math.Max(0, attempt - 1)));
        return TimeSpan.FromSeconds(seconds);
    }

    private async Task<bool> HasActiveOccurrenceAsync(string name, string scopeKey, CancellationToken cancellationToken)
    {
        var states = await _store.QueryAsync(new JobQuery { Name = name, Limit = 1000 }, cancellationToken).ConfigureAwait(false);
        return states.Any(s => OccurrenceMatchesScope(s.JobId, name, scopeKey) && s.Status is JobStatus.Queued or JobStatus.Scheduled or JobStatus.Processing);
    }

    // Exact scope match, not a JobId suffix test: an occurrence id is "{name}:{14-digit-timestamp}:{scopeKey}", and a
    // scope key (a node id) can itself contain ':' (NodeIdentity.Current is "{machine}:{pid}:{token}"), so a naive
    // EndsWith(":{scopeKey}") would let one node's occurrence count as another's. The query is already filtered to this
    // name, so strip the literal "{name}:" prefix and the fixed-width timestamp, then compare the remainder exactly.
    private static bool OccurrenceMatchesScope(string jobId, string name, string scopeKey)
    {
        string prefix = $"{name}:";
        if (!jobId.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var rest = jobId.AsSpan(prefix.Length);
        return rest.Length >= 15 && rest[14] == ':' && rest[15..].SequenceEqual(scopeKey);
    }

    private string GetScopeKey(ScheduledJobDefinition definition)
    {
        return definition.Scope == ScheduledJobScope.PerNode ? _nodeId : "global";
    }

    private static string CreateOccurrenceId(string name, DateTimeOffset scheduledForUtc, string scopeKey)
    {
        return $"{name}:{scheduledForUtc.UtcDateTime:yyyyMMddHHmmss}:{scopeKey}";
    }

    private static MessageHeaders CreateOccurrenceHeaders(ScheduledJobDefinition definition, DateTimeOffset scheduledForUtc, string scopeKey)
    {
        return MessageHeaders.Create([
            new KeyValuePair<string, string>("job.name", definition.Name),
            new KeyValuePair<string, string>("job.scheduled_for", scheduledForUtc.UtcDateTime.ToString("O")),
            new KeyValuePair<string, string>("job.scope", scopeKey)
        ]);
    }

    internal static void ValidateCron(string expression)
    {
        ParseCron(expression);
    }

    /// <summary>
    /// Parses a 5- or 6-field cron expression using the vendored Cronos parser. Six fields are interpreted as
    /// seconds-first (<see cref="CronFormat.IncludeSeconds"/>); five fields use the standard format. Cronos
    /// supports the full grammar (ranges, steps, lists, <c>L</c>/<c>W</c>/<c>#</c>, named months/days, and macros
    /// such as <c>@daily</c>).
    /// </summary>
    private static CronExpression ParseCron(string expression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);

        if (expression.StartsWith('@'))
            return CronExpression.Parse(expression, CronFormat.IncludeSeconds);

        int fieldCount = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
        var format = fieldCount == 6 ? CronFormat.IncludeSeconds : CronFormat.Standard;
        return CronExpression.Parse(expression, format);
    }
}
