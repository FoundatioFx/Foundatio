using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Cronos;
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
    public required string Name { get; init; }
    public required string Cron { get; init; }
    public Type? JobType { get; init; }
    public TimeZoneInfo? TimeZone { get; init; }
    public ScheduledJobScope Scope { get; init; } = ScheduledJobScope.Global;
    public OverlapPolicy Overlap { get; init; } = OverlapPolicy.SkipIfRunning;
    public TimeSpan? MisfireWindow { get; init; }
    public int MaxRetries { get; init; } = 3;

    /// <summary>
    /// Computes the delay before a failed occurrence is retried, given the attempt number (1-based).
    /// Defaults to capped exponential backoff when null.
    /// </summary>
    public Func<int, TimeSpan>? RetryBackoff { get; init; }

    public bool Enabled { get; init; } = true;
}

public interface IJobScheduler
{
    Task ScheduleAsync(ScheduledJobDefinition definition, CancellationToken cancellationToken = default);
    Task UnscheduleAsync(string name, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ScheduledJobDefinition>> GetSchedulesAsync(CancellationToken cancellationToken = default);
}

public sealed class InMemoryJobScheduler : IJobScheduler
{
    private readonly ConcurrentDictionary<string, ScheduledJobDefinition> _definitions = new(StringComparer.Ordinal);

    public Task ScheduleAsync(ScheduledJobDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrEmpty(definition.Name);
        ArgumentException.ThrowIfNullOrEmpty(definition.Cron);
        cancellationToken.ThrowIfCancellationRequested();

        if (definition.MaxRetries < 0)
            throw new ArgumentOutOfRangeException(nameof(definition), definition.MaxRetries, "MaxRetries must be greater than or equal to zero.");

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

public sealed class JobScheduleProcessor
{
    private static readonly TimeSpan DefaultLease = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultMisfireWindow = TimeSpan.FromMinutes(1);

    private readonly IJobScheduler _scheduler;
    private readonly IJobRuntimeStore _store;
    private readonly IJobWorker _jobWorker;
    private readonly TimeProvider _timeProvider;
    private readonly IJobTypeRegistry _jobTypes;
    private readonly string _nodeId;
    private readonly IMessageTransport? _transport;

    public JobScheduleProcessor(IJobScheduler scheduler, IJobRuntimeStore store, IJobWorker jobWorker, TimeProvider? timeProvider = null, string? nodeId = null, IMessageTransport? transport = null, IJobTypeRegistry? jobTypes = null)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _jobWorker = jobWorker ?? throw new ArgumentNullException(nameof(jobWorker));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _jobTypes = jobTypes ?? new JobTypeRegistry();
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
        var definitions = await _scheduler.GetSchedulesAsync(cancellationToken).ConfigureAwait(false);

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
                    Status = JobStatus.Scheduled,
                    CreatedUtc = utcNow,
                    LastUpdatedUtc = utcNow,
                    ScheduledForUtc = occurrence
                }, cancellationToken).ConfigureAwait(false);

                var dispatch = new ScheduledDispatchState
                {
                    DispatchId = jobId,
                    Kind = ScheduledDispatchKind.JobOccurrence,
                    Destination = definition.Name,
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

        var definitions = (await _scheduler.GetSchedulesAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(d => d.Name, StringComparer.Ordinal);

        var dispatches = await _store.ClaimDueDispatchesAsync(utcNow, limit, _nodeId, lease ?? DefaultLease, cancellationToken).ConfigureAwait(false);
        int completed = 0;

        foreach (var dispatch in dispatches)
        {
            if (dispatch.Kind is ScheduledDispatchKind.QueueMessage or ScheduledDispatchKind.PubSubMessage)
            {
                await MaterializeMessageDispatchAsync(dispatch, cancellationToken).ConfigureAwait(false);
                completed++;
                continue;
            }

            if (dispatch.Kind != ScheduledDispatchKind.JobOccurrence)
            {
                await _store.ReleaseDispatchAsync(dispatch.DispatchId, _nodeId, utcNow.AddMinutes(1), cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (!definitions.TryGetValue(dispatch.Destination, out var definition) || !definition.Enabled || definition.JobType is null)
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
                    if (state.Attempt <= definition.MaxRetries)
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

        var result = await _transport.SendAsync(dispatch.Destination, [
            new TransportMessage
            {
                MessageId = dispatch.DispatchId,
                Body = dispatch.Body,
                Headers = dispatch.Headers
            }
        ], dispatch.Options with { DeliverAt = null }, cancellationToken).ConfigureAwait(false);

        if (!result.AllSucceeded)
            throw new MessageBusException($"Unable to materialize scheduled dispatch \"{dispatch.DispatchId}\" to \"{dispatch.Destination}\".");

        await _store.CompleteDispatchAsync(dispatch.DispatchId, _nodeId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> TryPrepareOccurrenceForRunAsync(string jobId, ScheduledJobDefinition definition, DateTimeOffset utcNow, CancellationToken cancellationToken)
    {
        if (await _store.TryTransitionAsync(jobId, JobStatus.Scheduled, JobStatus.Queued, new JobStatePatch { JobType = GetJobTypeName(definition.JobType), LastUpdatedUtc = utcNow }, cancellationToken: cancellationToken).ConfigureAwait(false))
            return true;

        var state = await _store.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (state?.Status != JobStatus.Processing || state.LeaseExpiresUtc is null || state.LeaseExpiresUtc > utcNow)
            return false;

        if (state.Attempt > definition.MaxRetries)
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
