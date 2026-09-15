using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Cronos;
using Foundatio.Serializer;

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
    public required string JobType { get; init; }
    public string TimeZoneId { get; init; } = "UTC";
    public ScheduledJobScope Scope { get; init; } = ScheduledJobScope.Global;
    public OverlapPolicy Overlap { get; init; } = OverlapPolicy.SkipIfRunning;
    public TimeSpan? MisfireWindow { get; init; }
    /// <summary>Maximum TOTAL run attempts for a failed occurrence before it ends in Failed. Default 3.</summary>
    public int MaxAttempts { get; init; } = 3;
    public JobRetryPolicy RetryPolicy { get; init; } = new();
    /// <summary>Retires unclaimed per-node occurrences after this interval; active executions are unaffected.</summary>
    public TimeSpan UnclaimedLifetime { get; init; } = TimeSpan.FromDays(1);

    /// <summary>Serialized arguments copied into each occurrence.</summary>
    public ReadOnlyMemory<byte>? Payload { get; init; }
    public string? PayloadType { get; init; }
    /// <summary>Store revision. Read the latest definition before editing an existing schedule.</summary>
    public long Revision { get; init; }
    /// <summary>Increase to intentionally replace persisted schedule settings from declarative configuration.</summary>
    public int ConfigurationVersion { get; init; } = 1;

    public bool Enabled { get; init; } = true;

    /// <summary>Validates a serializable schedule before persisting it.</summary>
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(JobType);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxAttempts, 1);
        RetryPolicy.Validate();
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(UnclaimedLifetime, TimeSpan.Zero);
        if (!Enum.IsDefined(Scope)) throw new ArgumentOutOfRangeException(nameof(Scope));
        if (!Enum.IsDefined(Overlap)) throw new ArgumentOutOfRangeException(nameof(Overlap));
        ArgumentOutOfRangeException.ThrowIfNegative(Revision);
        ArgumentOutOfRangeException.ThrowIfNegative(ConfigurationVersion);
        if (MisfireWindow is { } window && (window < TimeSpan.Zero || window > TimeSpan.FromDays(1)))
            throw new ArgumentOutOfRangeException(nameof(MisfireWindow), "MisfireWindow must be between zero and one day.");
        JobScheduleProcessor.ValidateCron(Cron);
        _ = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
    }

}

/// <summary>
/// Options for a declaratively-registered CRON job — <c>AddFoundatio().Jobs.AddCronJob&lt;TJob&gt;(cron, o =&gt; ...)</c>.
/// The registered definitions are scheduled automatically when the explicitly registered job scheduler starts.
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

    /// <summary>Maximum TOTAL run attempts for a failed occurrence before reaching Failed. Default 3.</summary>
    public int MaxAttempts { get; set; } = 3;
    public JobRetryPolicy RetryPolicy { get; set; } = new();
    public TimeSpan UnclaimedLifetime { get; set; } = TimeSpan.FromDays(1);

    /// <summary>Whether the schedule is active. Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Time zone the CRON expression is evaluated in. Null uses the scheduler default (UTC).</summary>
    public TimeZoneInfo? TimeZone { get; set; }

    /// <summary>Increase when deploying an intentional change to this declared schedule.</summary>
    public int ConfigurationVersion { get; set; } = 1;
}

/// <summary>
/// Storage contract for scheduled (CRON) job definitions. Implementations persist the definitions themselves;
/// <see cref="IScheduledJobManager"/> is the user-facing management API layered on top of this store.
/// </summary>
public interface IScheduledJobStore
{
    /// <summary>Creates or updates a schedule, requiring the supplied Revision to match the stored revision.</summary>
    Task ScheduleAsync(ScheduledJobDefinition definition, CancellationToken cancellationToken = default);
    /// <summary>Applies a newer declared configuration; repeated or older deployments preserve persisted edits.</summary>
    Task ReconcileAsync(ScheduledJobDefinition definition, CancellationToken cancellationToken = default);
    Task<ScheduledJobDefinition?> GetScheduleAsync(string name, CancellationToken cancellationToken = default);
    Task UnscheduleAsync(string name, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ScheduledJobDefinition>> GetSchedulesAsync(ScheduleQuery? query = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Runtime management surface for scheduled (CRON) jobs: list and inspect schedules, add or replace definitions,
/// change a schedule's cron expression, enable/disable, and trigger an immediate occurrence. Declaratively-registered
/// jobs (<c>AddCronJob&lt;TJob&gt;</c>) and definitions added here share the same <see cref="IScheduledJobStore"/> store,
/// so both are manageable through this interface. The DI-configured manager requires job types to be registered
/// with <c>Jobs.AddJobType&lt;TJob&gt;()</c> before adding schedules.
/// </summary>
public interface IScheduledJobManager
{
    Task<IReadOnlyList<ScheduledJobDefinition>> GetSchedulesAsync(ScheduleQuery? query = null, CancellationToken cancellationToken = default);
    Task<ScheduledJobDefinition?> GetScheduleAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Adds a new schedule or replaces the existing definition with the same name.</summary>
    Task ScheduleAsync(ScheduledJobDefinition definition, CancellationToken cancellationToken = default);

    /// <summary>Creates a schedule for an argument-free job.</summary>
    Task ScheduleAsync<TJob>(string cron, Action<CronJobOptions>? configure = null, CancellationToken cancellationToken = default) where TJob : IJob;
    /// <summary>Creates a schedule with arguments constrained to the job contract.</summary>
    Task ScheduleAsync<TJob, TArgs>(string cron, TArgs arguments, Action<CronJobOptions>? configure = null, CancellationToken cancellationToken = default)
        where TJob : IJob<TArgs> where TArgs : class;

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
    /// runtime store and executed by a job worker) and uses the definition's retry budget and
    /// serialized arguments. Manual occurrences respect the configured overlap policy.
    /// Throws when the schedule does not exist, is disabled, or already has active work that excludes overlap.
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
    private readonly string? _nodeId;
    private readonly IScheduledJobStore _scheduleStore;
    private readonly IJobRuntimeStore _store;
    private readonly IJobTypeRegistry _jobTypes;
    private readonly bool _requireRegisteredTypes;
    private readonly ISerializer _serializer;
    private readonly TimeProvider _timeProvider;

    public ScheduledJobManager(IScheduledJobStore scheduleStore, IJobRuntimeStore store, IJobTypeRegistry? jobTypes = null, ISerializer? serializer = null, TimeProvider? timeProvider = null, string? nodeId = null)
    {
        _nodeId = nodeId;
        _scheduleStore = scheduleStore ?? throw new ArgumentNullException(nameof(scheduleStore));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _jobTypes = jobTypes ?? new JobTypeRegistry();
        _requireRegisteredTypes = jobTypes is not null;
        _serializer = serializer ?? DefaultSerializer.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<IReadOnlyList<ScheduledJobDefinition>> GetSchedulesAsync(ScheduleQuery? query = null, CancellationToken cancellationToken = default)
        => _scheduleStore.GetSchedulesAsync(query, cancellationToken);

    public Task<ScheduledJobDefinition?> GetScheduleAsync(string name, CancellationToken cancellationToken = default)
        => _scheduleStore.GetScheduleAsync(name, cancellationToken);

    public Task ScheduleAsync(ScheduledJobDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (_requireRegisteredTypes && definition.JobType is not null)
            _jobTypes.Resolve(definition.JobType);
        return _scheduleStore.ScheduleAsync(definition, cancellationToken);
    }

    public Task ScheduleAsync<TJob>(string cron, Action<CronJobOptions>? configure = null, CancellationToken cancellationToken = default) where TJob : IJob
        => ScheduleAsync(typeof(TJob), cron, null, configure, cancellationToken);

    public Task ScheduleAsync<TJob, TArgs>(string cron, TArgs arguments, Action<CronJobOptions>? configure = null, CancellationToken cancellationToken = default)
        where TJob : IJob<TArgs> where TArgs : class
        => ScheduleAsync(typeof(TJob), cron, arguments, configure, cancellationToken);

    private Task ScheduleAsync(Type jobType, string cron, object? arguments, Action<CronJobOptions>? configure, CancellationToken cancellationToken)
    {
        var options = new CronJobOptions();
        configure?.Invoke(options);
        return ScheduleAsync(new ScheduledJobRegistration(jobType, cron, options, arguments).Create(_jobTypes, _serializer), cancellationToken);
    }

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

        var occurrence = new JobState
        {
            JobId = jobId,
            Name = definition.Name,
            ScheduleName = definition.Name,
            JobType = definition.JobType,
            MaxAttempts = definition.MaxAttempts,
            RequiredNodeId = definition.Scope == ScheduledJobScope.PerNode ? NodeIdentity.RequireStable(_nodeId) : null,
            ExpiresUtc = definition.Scope == ScheduledJobScope.PerNode ? now.Add(definition.UnclaimedLifetime) : null,
            RetryPolicy = definition.RetryPolicy,
            Payload = definition.Payload,
            PayloadType = definition.PayloadType,
            Status = JobStatus.Queued,
            CreatedUtc = now,
            LastUpdatedUtc = now,
            ScheduledForUtc = now
        };
        if (await _store.CreateOccurrenceAsync(occurrence, definition.Overlap == OverlapPolicy.AllowConcurrent, cancellationToken).ConfigureAwait(false) != JobOccurrenceResult.Created)
            throw new JobException($"Scheduled job {name} already has pending or running work.");

        return new JobHandle(jobId, _store, _store.RequestCancellationAsync, _timeProvider);
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
}

public sealed class JobScheduleProcessor
{
    private static readonly TimeSpan DefaultMisfireWindow = TimeSpan.FromMinutes(1);

    private readonly IScheduledJobStore _scheduleStore;
    private readonly IJobRuntimeStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly string? _nodeId;
    private readonly ConcurrentDictionary<string, CachedSchedule> _cache = new(StringComparer.Ordinal);
    private sealed class CachedSchedule(ScheduledJobDefinition definition)
    {
        public ScheduledJobDefinition Definition { get; } = definition;
        public CronExpression Cron { get; } = ParseCron(definition.Cron);
        public TimeZoneInfo TimeZone { get; } = TimeZoneInfo.FindSystemTimeZoneById(definition.TimeZoneId);
        private long _lastConfirmedTicks;
        public DateTimeOffset LastConfirmed => new(Interlocked.Read(ref _lastConfirmedTicks), TimeSpan.Zero);
        public void Confirm(DateTimeOffset occurrence)
        {
            long ticks = occurrence.UtcTicks;
            long previous;
            do { previous = Interlocked.Read(ref _lastConfirmedTicks); if (previous >= ticks) return; }
            while (Interlocked.CompareExchange(ref _lastConfirmedTicks, ticks, previous) != previous);
        }
    }

    public JobScheduleProcessor(IScheduledJobStore scheduleStore, IJobRuntimeStore store, JobScheduleProcessorOptions? options = null)
    {
        _scheduleStore = scheduleStore ?? throw new ArgumentNullException(nameof(scheduleStore));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeProvider = options?.TimeProvider ?? TimeProvider.System;
        _nodeId = options?.NodeId ?? NodeIdentity.Configured;
    }

    public Task<IReadOnlyList<JobState>> EnqueueDueOccurrencesAsync(CancellationToken cancellationToken = default)
    {
        return EnqueueDueOccurrencesAsync(_timeProvider.GetUtcNow(), cancellationToken);
    }

    public async Task<IReadOnlyList<JobState>> EnqueueDueOccurrencesAsync(DateTimeOffset utcNow, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var scheduled = new List<JobState>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var definition in EnumerateSchedulesAsync(cancellationToken).ConfigureAwait(false))
        {
            seen.Add(definition.Name);
            if (!definition.Enabled)
                continue;

            var cached = _cache.AddOrUpdate(definition.Name, _ => new CachedSchedule(definition),
                (_, previous) => previous.Definition.Revision == definition.Revision && previous.Definition.Cron == definition.Cron && previous.Definition.TimeZoneId == definition.TimeZoneId ? previous : new CachedSchedule(definition));
            var cron = cached.Cron;
            var timeZone = cached.TimeZone;
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
                occurrences = [occurrences[^1]];

            foreach (var occurrence in occurrences)
            {
                if (occurrence <= cached.LastConfirmed)
                    continue;
                var state = new JobState
                {
                    JobId = CreateOccurrenceId(definition.Name, occurrence, scopeKey),
                    Name = definition.Name,
                    ScheduleName = definition.Name,
                    JobType = definition.JobType,
                    MaxAttempts = definition.MaxAttempts,
                    RequiredNodeId = definition.Scope == ScheduledJobScope.PerNode ? NodeIdentity.RequireStable(_nodeId) : null,
                    ExpiresUtc = definition.Scope == ScheduledJobScope.PerNode ? utcNow.Add(definition.UnclaimedLifetime) : null,
                    RetryPolicy = definition.RetryPolicy,
                    Payload = definition.Payload,
                    PayloadType = definition.PayloadType,
                    Status = JobStatus.Queued,
                    CreatedUtc = utcNow,
                    LastUpdatedUtc = utcNow,
                    ScheduledForUtc = occurrence
                };
                var result = await _store.CreateOccurrenceAsync(state, definition.Overlap == OverlapPolicy.AllowConcurrent, cancellationToken).ConfigureAwait(false);
                if (result != JobOccurrenceResult.OverlapBlocked)
                    cached.Confirm(occurrence);
                if (result == JobOccurrenceResult.Created)
                    scheduled.Add(state);
            }
        }

        foreach (string name in _cache.Keys)
            if (!seen.Contains(name)) _cache.TryRemove(name, out _);
        return scheduled;
    }

    private async IAsyncEnumerable<ScheduledJobDefinition> EnumerateSchedulesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? afterName = null;
        while (true)
        {
            var page = await _scheduleStore.GetSchedulesAsync(new ScheduleQuery { AfterName = afterName }, cancellationToken).ConfigureAwait(false);
            foreach (var definition in page)
                yield return definition;
            if (page.Count < 100)
                yield break;
            afterName = page[^1].Name;
        }
    }

    private string GetScopeKey(ScheduledJobDefinition definition)
    {
        return definition.Scope == ScheduledJobScope.PerNode ? NodeIdentity.RequireStable(_nodeId) : "global";
    }

    private static string CreateOccurrenceId(string name, DateTimeOffset scheduledForUtc, string scopeKey)
    {
        return $"{name}:{scheduledForUtc.UtcDateTime:yyyyMMddHHmmss}:{scopeKey}";
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
