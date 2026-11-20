using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;
using Foundatio.Utility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Foundatio.Monitoring;

/// <summary>Merges this process's live data with the other instances' snapshots, and adds backlog and job counts.</summary>
internal sealed class WorkMonitor : IWorkMonitor
{
    private static readonly TimeSpan CountTimeout = TimeSpan.FromSeconds(10);
    private static readonly JobState[] CountedStates = [JobState.Queued, JobState.Running, JobState.Succeeded, JobState.Failed, JobState.Cancelled];

    private readonly MonitoringRuntime _runtime;
    private readonly IServiceProvider _services;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private CachedRead<MessageSourceStats>? _backlog;
    private CachedRead<JobCounts>? _jobCounts;

    public WorkMonitor(MonitoringRuntime runtime, IServiceProvider services)
    {
        _runtime = runtime;
        _services = services;
        _logger = runtime.LoggerFactory.CreateLogger<WorkMonitor>();
    }

    public async Task<WorkOverview> GetOverviewAsync(WorkFilter? filter = null, CancellationToken cancellationToken = default)
    {
        var snapshots = await LoadSnapshotsAsync(cancellationToken).AnyContext();
        var backlog = GetBacklogAsync(snapshots, cancellationToken);
        var jobs = GetJobCountsAsync(snapshots, cancellationToken);

        return new WorkOverview
        {
            GeneratedAt = _runtime.TimeProvider.GetUtcNow(),
            Instances = ToInstances(filter?.InstanceId is { } instanceId ? snapshots.Where(s => s.InstanceId == instanceId).ToList() : snapshots),
            Active = ToActive(snapshots, filter),
            Recent = ToRecent(snapshots, filter),
            Stats = ToStats(snapshots, filter),
            Backlog = await backlog.AnyContext(),
            Jobs = await jobs.AnyContext()
        };
    }

    public async Task<IReadOnlyList<WorkInstance>> GetInstancesAsync(CancellationToken cancellationToken = default)
        => ToInstances(await LoadSnapshotsAsync(cancellationToken).AnyContext());

    public async Task<IReadOnlyList<ActiveWork>> GetActiveAsync(WorkFilter? filter = null, CancellationToken cancellationToken = default)
        => ToActive(await LoadSnapshotsAsync(cancellationToken).AnyContext(), filter);

    public async Task<IReadOnlyList<CompletedWork>> GetRecentAsync(WorkFilter? filter = null, CancellationToken cancellationToken = default)
        => ToRecent(await LoadSnapshotsAsync(cancellationToken).AnyContext(), filter);

    public async Task<IReadOnlyList<WorkStats>> GetStatsAsync(WorkFilter? filter = null, CancellationToken cancellationToken = default)
        => ToStats(await LoadSnapshotsAsync(cancellationToken).AnyContext(), filter);

    public async Task<IReadOnlyList<MessageSourceStats>> GetBacklogAsync(CancellationToken cancellationToken = default)
        => await GetBacklogAsync(await LoadSnapshotsAsync(cancellationToken).AnyContext(), cancellationToken).AnyContext();

    public async Task<IReadOnlyList<JobCounts>> GetJobCountsAsync(CancellationToken cancellationToken = default)
        => await GetJobCountsAsync(await LoadSnapshotsAsync(cancellationToken).AnyContext(), cancellationToken).AnyContext();

    /// <summary>This process's live snapshot, when it handles anything, followed by every other instance's latest one.</summary>
    private async Task<IReadOnlyList<InstanceSnapshot>> LoadSnapshotsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshots = new List<InstanceSnapshot>();
        if (_runtime.HasWork)
            snapshots.Add(_runtime.CreateSnapshot());

        if (_runtime.Cache is not { } cache)
            return snapshots;

        var members = await cache.GetListAsync<string>(_runtime.IndexKey).AnyContext();
        if (!members.HasValue)
            return snapshots;

        var ids = members.Value.Where(id => !String.IsNullOrEmpty(id) && id != _runtime.InstanceId).Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0)
            return snapshots;

        cancellationToken.ThrowIfCancellationRequested();
        var values = await cache.GetAllAsync<string>(ids.Select(_runtime.GetInstanceKey)).AnyContext();
        foreach (string id in ids)
        {
            if (values.TryGetValue(_runtime.GetInstanceKey(id), out var value) && value.HasValue && Deserialize(id, value.Value) is { } snapshot)
                snapshots.Add(snapshot);
        }

        return snapshots;
    }

    private InstanceSnapshot? Deserialize(string id, string? json)
    {
        if (String.IsNullOrEmpty(json))
            return null;

        try
        {
            var snapshot = JsonSerializer.Deserialize(json, MonitoringJsonContext.Default.InstanceSnapshot);
            if (snapshot?.Version == InstanceSnapshot.CurrentVersion && snapshot.InstanceId == id)
                return snapshot;

            _logger.LogDebug("Ignoring the monitoring snapshot of instance {InstanceId}: version {Version} is not {CurrentVersion}", id, snapshot?.Version, InstanceSnapshot.CurrentVersion);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Ignoring the monitoring snapshot of instance {InstanceId}: it could not be read", id);
        }

        return null;
    }

    private IReadOnlyList<WorkInstance> ToInstances(IReadOnlyList<InstanceSnapshot> snapshots)
    {
        return snapshots
            .Select(s => new WorkInstance
            {
                InstanceId = s.InstanceId,
                ServiceName = s.ServiceName,
                MachineName = s.MachineName,
                ProcessId = s.ProcessId,
                StartedAt = s.StartedAt,
                UpdatedAt = s.UpdatedAt,
                MessageSources = s.MessageSources.Select(MessageSourceNames.TryParse).OfType<MessageSource>().ToList(),
                JobNames = s.JobNames,
                IsCurrent = s.InstanceId == _runtime.InstanceId
            })
            .OrderBy(i => i.ServiceName, StringComparer.Ordinal)
            .ThenBy(i => i.InstanceId, StringComparer.Ordinal)
            .ToList();
    }

    private static IReadOnlyList<ActiveWork> ToActive(IReadOnlyList<InstanceSnapshot> snapshots, WorkFilter? filter)
    {
        return Filter(snapshots, filter)
            .SelectMany(s => s.Active.Where(a => Matches(filter, a.Kind, a.Name)).Select(a => new ActiveWork
            {
                Kind = a.Kind,
                Id = a.Id,
                Name = a.Name,
                Source = a.Source,
                Handler = a.Handler,
                Attempt = a.Attempt,
                CorrelationId = a.CorrelationId,
                StartedAt = a.StartedAt,
                Progress = a.Progress,
                ProgressMessage = a.ProgressMessage,
                ProgressAt = a.ProgressAt,
                InstanceId = s.InstanceId
            }))
            .OrderBy(a => a.StartedAt)
            .ToList();
    }

    private static IReadOnlyList<CompletedWork> ToRecent(IReadOnlyList<InstanceSnapshot> snapshots, WorkFilter? filter)
    {
        return Filter(snapshots, filter)
            .SelectMany(s => s.Recent.Where(r => Matches(filter, r.Kind, r.Name)).Select(r => new CompletedWork
            {
                Kind = r.Kind,
                Id = r.Id,
                Name = r.Name,
                Source = r.Source,
                Handler = r.Handler,
                Attempt = r.Attempt,
                CorrelationId = r.CorrelationId,
                StartedAt = r.StartedAt,
                CompletedAt = r.CompletedAt,
                Duration = r.Duration,
                Outcome = r.Outcome,
                Error = WorkTracker.Truncate(r.Error),
                Progress = r.Progress,
                ProgressMessage = r.ProgressMessage,
                InstanceId = s.InstanceId
            }))
            .OrderByDescending(r => r.CompletedAt)
            .ToList();
    }

    private static IReadOnlyList<WorkStats> ToStats(IReadOnlyList<InstanceSnapshot> snapshots, WorkFilter? filter)
    {
        return Filter(snapshots, filter)
            .SelectMany(s => s.Stats.Where(e => Matches(filter, e.Kind, e.Name)).Select(e => (Entry: e, s.StatsWindow)))
            .GroupBy(e => (e.Entry.Kind, e.Entry.Name, e.Entry.Source))
            .Select(group =>
            {
                long timed = group.Sum(e => e.Entry.Timed);
                long totalTicks = group.Sum(e => e.Entry.TotalDuration.Ticks);
                return new WorkStats
                {
                    Kind = group.Key.Kind,
                    Name = group.Key.Name,
                    Source = group.Key.Source,
                    Succeeded = group.Sum(e => e.Entry.Succeeded),
                    Retried = group.Sum(e => e.Entry.Retried),
                    Failed = group.Sum(e => e.Entry.Failed),
                    DeadLettered = group.Sum(e => e.Entry.DeadLettered),
                    Cancelled = group.Sum(e => e.Entry.Cancelled),
                    Skipped = group.Sum(e => e.Entry.Skipped),
                    InFlight = group.Sum(e => e.Entry.InFlight),
                    AverageDuration = timed > 0 ? TimeSpan.FromTicks(totalTicks / timed) : null,
                    MaxDuration = timed > 0 ? group.Max(e => e.Entry.MaxDuration) : null,
                    Window = group.Max(e => e.StatsWindow)
                };
            })
            .OrderBy(s => s.Kind)
            .ThenBy(s => s.Name, StringComparer.Ordinal)
            .ThenBy(s => s.Source, StringComparer.Ordinal)
            .ToList();
    }

    private static IEnumerable<InstanceSnapshot> Filter(IReadOnlyList<InstanceSnapshot> snapshots, WorkFilter? filter)
        => filter?.InstanceId is { } instanceId ? snapshots.Where(s => s.InstanceId == instanceId) : snapshots;

    private static bool Matches(WorkFilter? filter, WorkKind kind, string name)
        => filter is null || (filter.Kind is null || filter.Kind == kind) && (filter.Name is null || String.Equals(filter.Name, name, StringComparison.Ordinal));

    private Task<IReadOnlyList<MessageSourceStats>> GetBacklogAsync(IReadOnlyList<InstanceSnapshot> snapshots, CancellationToken cancellationToken)
    {
        if (_services.GetService<IMessageAdmin>() is not { } admin)
            return Task.FromResult<IReadOnlyList<MessageSourceStats>>([]);

        var sources = snapshots
            .SelectMany(s => s.MessageSources)
            .Select(MessageSourceNames.TryParse)
            .OfType<MessageSource>()
            .Where(s => s.Kind is MessageSourceKind.Queue or MessageSourceKind.Subscription)
            .DistinctBy(s => s.ToString())
            .OrderBy(s => s.ToString(), StringComparer.Ordinal)
            .ToList();

        return ReadCachedAsync(ref _backlog, String.Join("\n", sources), () => CountSourcesAsync(admin, sources), cancellationToken);
    }

    private Task<IReadOnlyList<JobCounts>> GetJobCountsAsync(IReadOnlyList<InstanceSnapshot> snapshots, CancellationToken cancellationToken)
    {
        if (_services.GetService<IJobClient>() is not { } jobs)
            return Task.FromResult<IReadOnlyList<JobCounts>>([]);

        var names = snapshots.SelectMany(s => s.JobNames).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return ReadCachedAsync(ref _jobCounts, String.Join("\n", names), () => CountJobsAsync(jobs, names), cancellationToken);
    }

    /// <summary>
    /// Returns the cached read when it is for the same <paramref name="key"/> and younger than the snapshot interval;
    /// otherwise starts one read that concurrent callers share. A caller's cancellation stops its wait, not the read.
    /// </summary>
    private Task<IReadOnlyList<T>> ReadCachedAsync<T>(ref CachedRead<T>? cached, string key, Func<Task<IReadOnlyList<T>>> read, CancellationToken cancellationToken)
    {
        Task<IReadOnlyList<T>> task;
        lock (_lock)
        {
            long now = _runtime.TimeProvider.GetTimestamp();
            if (cached is not { } current || current.Key != key || current.Task.IsFaulted || _runtime.TimeProvider.GetElapsedTime(current.StartedAt, now) >= _runtime.Options.SnapshotInterval)
                cached = current = new CachedRead<T>(key, now, Task.Run(read, CancellationToken.None));

            task = current.Task;
        }

        return task.WaitAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<MessageSourceStats>> CountSourcesAsync(IMessageAdmin admin, IReadOnlyList<MessageSource> sources)
    {
        return await Task.WhenAll(sources.Select(async source =>
        {
            using var timeout = new CancellationTokenSource(CountTimeout);
            try
            {
                return await admin.GetStatsAsync(source, timeout.Token).AnyContext() ?? new MessageSourceStats { Source = source };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not count the messages of {Source} for monitoring", source);
                return new MessageSourceStats { Source = source };
            }
        })).AnyContext();
    }

    private async Task<IReadOnlyList<JobCounts>> CountJobsAsync(IJobClient jobs, IReadOnlyList<string> names)
    {
        return await Task.WhenAll(names.Select(async name =>
        {
            using var timeout = new CancellationTokenSource(CountTimeout);
            try
            {
                var counts = new long[CountedStates.Length];
                for (int i = 0; i < CountedStates.Length; i++)
                    counts[i] = await jobs.CountAsync(CountedStates[i], name, timeout.Token).AnyContext();

                return new JobCounts { Name = name, Queued = counts[0], Running = counts[1], Succeeded = counts[2], Failed = counts[3], Cancelled = counts[4] };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not count the jobs named {JobName} for monitoring", name);
                return new JobCounts { Name = name };
            }
        })).AnyContext();
    }

    private sealed record CachedRead<T>(string Key, long StartedAt, Task<IReadOnlyList<T>> Task);
}
