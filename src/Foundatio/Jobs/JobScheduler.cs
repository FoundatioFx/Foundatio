using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
    private readonly string _nodeId;
    private readonly IMessageTransport? _transport;

    public JobScheduleProcessor(IJobScheduler scheduler, IJobRuntimeStore store, IJobWorker jobWorker, TimeProvider? timeProvider = null, string? nodeId = null, IMessageTransport? transport = null)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _jobWorker = jobWorker ?? throw new ArgumentNullException(nameof(jobWorker));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _nodeId = !String.IsNullOrEmpty(nodeId)
            ? nodeId
            : Environment.GetEnvironmentVariable("FOUNDATIO_NODE_ID") ?? Environment.MachineName;
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

            var cron = CronSchedule.Parse(definition.Cron);
            var scheduledForUtc = cron.GetLastOccurrence(utcNow, definition.TimeZone ?? TimeZoneInfo.Utc, definition.MisfireWindow ?? DefaultMisfireWindow);
            if (scheduledForUtc is null)
                continue;

            string scopeKey = GetScopeKey(definition);
            string jobId = CreateOccurrenceId(definition.Name, scheduledForUtc.Value, scopeKey);

            if (await _store.GetAsync(jobId, cancellationToken).ConfigureAwait(false) is not null)
                continue;

            if (definition.Overlap == OverlapPolicy.SkipIfRunning && await HasActiveOccurrenceAsync(definition.Name, scopeKey, cancellationToken).ConfigureAwait(false))
                continue;

            await _store.CreateIfAbsentAsync(new JobState
            {
                JobId = jobId,
                Name = definition.Name,
                JobType = definition.JobType?.AssemblyQualifiedName,
                Status = JobStatus.Scheduled,
                CreatedUtc = utcNow,
                LastUpdatedUtc = utcNow,
                ScheduledForUtc = scheduledForUtc
            }, cancellationToken).ConfigureAwait(false);

            var dispatch = new ScheduledDispatchState
            {
                DispatchId = jobId,
                Kind = ScheduledDispatchKind.JobOccurrence,
                Destination = definition.Name,
                Body = Array.Empty<byte>(),
                Headers = CreateOccurrenceHeaders(definition, scheduledForUtc.Value, scopeKey),
                DueUtc = utcNow,
                JobId = jobId
            };

            await _store.ScheduleDispatchAsync(dispatch, cancellationToken).ConfigureAwait(false);
            scheduled.Add(dispatch);
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
                        }, cancellationToken).ConfigureAwait(false);
                        await _store.ReleaseDispatchAsync(dispatch.DispatchId, _nodeId, utcNow.AddMinutes(1), cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    await _store.TryTransitionAsync(jobId, JobStatus.Failed, JobStatus.DeadLettered, new JobStatePatch
                    {
                        ClearNodeId = true,
                        ClearLeaseExpiresUtc = true,
                        LastUpdatedUtc = utcNow
                    }, cancellationToken).ConfigureAwait(false);
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
        if (await _store.TryTransitionAsync(jobId, JobStatus.Scheduled, JobStatus.Queued, new JobStatePatch { JobType = definition.JobType?.AssemblyQualifiedName, LastUpdatedUtc = utcNow }, cancellationToken).ConfigureAwait(false))
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
            }, cancellationToken).ConfigureAwait(false);
            return false;
        }

        return await _store.TryTransitionAsync(jobId, JobStatus.Processing, JobStatus.Queued, new JobStatePatch
        {
            JobType = definition.JobType?.AssemblyQualifiedName,
            ClearNodeId = true,
            ClearLeaseExpiresUtc = true,
            LastUpdatedUtc = utcNow
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> HasActiveOccurrenceAsync(string name, string scopeKey, CancellationToken cancellationToken)
    {
        var states = await _store.QueryAsync(new JobQuery { Name = name, Limit = 1000 }, cancellationToken).ConfigureAwait(false);
        return states.Any(s => s.JobId.EndsWith($":{scopeKey}", StringComparison.Ordinal) && s.Status is JobStatus.Queued or JobStatus.Scheduled or JobStatus.Processing);
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
        CronSchedule.Parse(expression);
    }

    private sealed class CronSchedule
    {
        private readonly CronFieldSet _second;
        private readonly CronFieldSet _minute;
        private readonly CronFieldSet _hour;
        private readonly CronFieldSet _dayOfMonth;
        private readonly CronFieldSet _month;
        private readonly CronFieldSet _dayOfWeek;

        private CronSchedule(CronFieldSet second, CronFieldSet minute, CronFieldSet hour, CronFieldSet dayOfMonth, CronFieldSet month, CronFieldSet dayOfWeek)
        {
            _second = second;
            _minute = minute;
            _hour = hour;
            _dayOfMonth = dayOfMonth;
            _month = month;
            _dayOfWeek = dayOfWeek;
        }

        public static CronSchedule Parse(string expression)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(expression);

            var parts = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 5 && parts.Length != 6)
                throw new FormatException("Cron expressions must contain five fields, or six fields when seconds are included.");

            int offset = parts.Length == 6 ? 0 : -1;
            return new CronSchedule(
                offset == 0 ? CronFieldSet.Parse(parts[0], 0, 59) : CronFieldSet.Single(0, 0, 59),
                CronFieldSet.Parse(parts[1 + offset], 0, 59),
                CronFieldSet.Parse(parts[2 + offset], 0, 23),
                CronFieldSet.Parse(parts[3 + offset], 1, 31, allowQuestion: true),
                CronFieldSet.Parse(parts[4 + offset], 1, 12),
                CronFieldSet.Parse(parts[5 + offset], 0, 6, allowQuestion: true, normalizeDayOfWeek: true));
        }

        public DateTimeOffset? GetLastOccurrence(DateTimeOffset utcNow, TimeZoneInfo timeZone, TimeSpan misfireWindow)
        {
            if (misfireWindow < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(misfireWindow), misfireWindow, "MisfireWindow must be greater than or equal to zero.");

            var localNow = TimeZoneInfo.ConvertTime(utcNow, timeZone);
            var candidate = new DateTimeOffset(localNow.Year, localNow.Month, localNow.Day, localNow.Hour, localNow.Minute, localNow.Second, localNow.Offset);
            int secondsToSearch = Math.Max(1, (int)Math.Ceiling(misfireWindow.TotalSeconds)) + 1;

            for (int i = 0; i <= secondsToSearch; i++)
            {
                if (Matches(candidate.DateTime))
                    return TimeZoneInfo.ConvertTime(candidate, TimeZoneInfo.Utc);

                candidate = candidate.AddSeconds(-1);
            }

            return null;
        }

        private bool Matches(DateTime local)
        {
            if (!_second.Contains(local.Second) || !_minute.Contains(local.Minute) || !_hour.Contains(local.Hour) || !_month.Contains(local.Month))
                return false;

            bool dayOfMonthMatches = _dayOfMonth.Contains(local.Day);
            bool dayOfWeekMatches = _dayOfWeek.Contains((int)local.DayOfWeek);
            return _dayOfMonth.IsAny || _dayOfWeek.IsAny
                ? dayOfMonthMatches && dayOfWeekMatches
                : dayOfMonthMatches || dayOfWeekMatches;
        }
    }

    private sealed class CronFieldSet
    {
        private readonly bool[] _values;
        private readonly int _min;

        private CronFieldSet(bool[] values, int min, bool isAny)
        {
            _values = values;
            _min = min;
            IsAny = isAny;
        }

        public bool IsAny { get; }

        public static CronFieldSet Single(int value, int min, int max)
        {
            var values = new bool[max - min + 1];
            values[value - min] = true;
            return new CronFieldSet(values, min, false);
        }

        public static CronFieldSet Parse(string expression, int min, int max, bool allowQuestion = false, bool normalizeDayOfWeek = false)
        {
            if (expression == "*" || (allowQuestion && expression == "?"))
                return Any(min, max);

            var values = new bool[max - min + 1];
            foreach (string segment in expression.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                AddSegment(values, segment, min, max, allowQuestion, normalizeDayOfWeek);

            return new CronFieldSet(values, min, false);
        }

        public bool Contains(int value)
        {
            int index = value - _min;
            return index >= 0 && index < _values.Length && _values[index];
        }

        private static CronFieldSet Any(int min, int max)
        {
            var values = new bool[max - min + 1];
            Array.Fill(values, true);
            return new CronFieldSet(values, min, true);
        }

        private static void AddSegment(bool[] values, string segment, int min, int max, bool allowQuestion, bool normalizeDayOfWeek)
        {
            string[] stepParts = segment.Split('/', StringSplitOptions.TrimEntries);
            if (stepParts.Length > 2)
                throw new FormatException($"Invalid cron field segment '{segment}'.");

            int step = stepParts.Length == 2 ? ParseNumber(stepParts[1], 1, max) : 1;
            string range = stepParts[0];

            if (range == "*" || (allowQuestion && range == "?"))
            {
                AddRange(values, min, max, step, min, normalizeDayOfWeek);
                return;
            }

            string[] rangeParts = range.Split('-', StringSplitOptions.TrimEntries);
            if (rangeParts.Length == 1)
            {
                int value = Normalize(ParseNumber(rangeParts[0], min, normalizeDayOfWeek ? max + 1 : max), normalizeDayOfWeek);
                EnsureInRange(value, min, max);
                values[value - min] = true;
                return;
            }

            if (rangeParts.Length != 2)
                throw new FormatException($"Invalid cron field segment '{segment}'.");

            int start = Normalize(ParseNumber(rangeParts[0], min, normalizeDayOfWeek ? max + 1 : max), normalizeDayOfWeek);
            int end = Normalize(ParseNumber(rangeParts[1], min, normalizeDayOfWeek ? max + 1 : max), normalizeDayOfWeek);
            EnsureInRange(start, min, max);
            EnsureInRange(end, min, max);

            if (end < start)
                throw new FormatException($"Invalid cron range '{segment}'.");

            AddRange(values, start, end, step, min, normalizeDayOfWeek);
        }

        private static void AddRange(bool[] values, int start, int end, int step, int min, bool normalizeDayOfWeek)
        {
            for (int value = start; value <= end; value += step)
            {
                int normalized = Normalize(value, normalizeDayOfWeek);
                values[normalized - min] = true;
            }
        }

        private static int ParseNumber(string value, int min, int max)
        {
            if (!Int32.TryParse(value, out int result) || result < min || result > max)
                throw new FormatException($"Cron value '{value}' must be between {min} and {max}.");

            return result;
        }

        private static int Normalize(int value, bool normalizeDayOfWeek)
        {
            return normalizeDayOfWeek && value == 7 ? 0 : value;
        }

        private static void EnsureInRange(int value, int min, int max)
        {
            if (value < min || value > max)
                throw new FormatException($"Cron value '{value}' must be between {min} and {max}.");
        }
    }
}
