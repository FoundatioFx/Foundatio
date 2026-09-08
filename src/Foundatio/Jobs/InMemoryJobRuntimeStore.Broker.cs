using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Jobs;

public sealed partial class InMemoryJobRuntimeStore
{
    public bool IsShared => false;
    private readonly SortedSet<(DateTimeOffset Expires, string Id)> _brokerExpiry = new();
    private readonly Dictionary<(string Name, DateTimeOffset Hour), Dictionary<string, long>> _counters = new();

    public Task<JobState?> BeginBrokerAttemptAsync(string jobId, int attempt, string nodeId, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            PurgeBrokerHistory();
            if (!_jobs.TryGetValue(jobId, out var state) || state.ExecutionOwner != JobExecutionOwner.Broker || !IsActive(state) || attempt <= state.Attempt)
                return Task.FromResult<JobState?>(null);
            var now = _timeProvider.GetUtcNow();
            StoreJob(state with
            {
                Status = JobStatus.Processing,
                Attempt = attempt,
                ClaimToken = Guid.NewGuid().ToString("N"),
                NodeId = nodeId,
                StartedUtc = now,
                LastHeartbeatUtc = now,
                LastUpdatedUtc = now,
                CompletedUtc = null,
                Progress = 0,
                ProgressMessage = null
            });
            return Task.FromResult<JobState?>(_jobs[jobId]);
        }
    }

    public Task<bool> MarkEnqueueUnknownAsync(string jobId, string error, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            PurgeBrokerHistory();
            if (!_jobs.TryGetValue(jobId, out var state) || state.ExecutionOwner != JobExecutionOwner.Broker || state.Status != JobStatus.Queued || state.Attempt != 0)
                return Task.FromResult(false);
            StoreJob(state with { Status = JobStatus.EnqueueUnknown, Error = error, LastUpdatedUtc = _timeProvider.GetUtcNow() });
            return Task.FromResult(true);
        }
    }

    public Task<bool> HeartbeatJobAsync(string jobId, string claimToken, CancellationToken cancellationToken = default)
        => ReportJobProgressAsync(jobId, claimToken, cancellationToken: cancellationToken);

    public Task<bool> RemoveAsync(string jobId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            PurgeBrokerHistory();
            if (!_jobs.TryGetValue(jobId, out var state) || state.ExecutionOwner == JobExecutionOwner.Runtime && IsActive(state))
                return Task.FromResult(false);
            ForgetJob(state);
            return Task.FromResult(true);
        }
    }

    private void ForgetJob(JobState state)
    {
        if (state.HistoryExpiresUtc is { } expires) _brokerExpiry.Remove((expires, state.JobId));
        _jobs.TryRemove(state.JobId, out _);
        if (IsActive(state)) _activeJobs--;
        _active.Remove(state.JobId);
        if (state.ExecutionOwner == JobExecutionOwner.Broker) _deduplication.Remove(state.JobId);
    }

    private void PurgeBrokerHistory()
    {
        var now = _timeProvider.GetUtcNow();
        while (_brokerExpiry.Count > 0 && _brokerExpiry.Min.Expires <= now)
        {
            var item = _brokerExpiry.Min;
            _brokerExpiry.Remove(item);
            if (_jobs.TryGetValue(item.Id, out var state) && state.HistoryExpiresUtc == item.Expires) ForgetJob(state);
        }
    }

    public Task IncrementCounterAsync(string name, string counterName, long value = 1, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            var hour = TruncateHour(_timeProvider.GetUtcNow());
            foreach (var key in _counters.Keys.Where(k => k.Hour < hour.AddHours(-48)).ToArray()) _counters.Remove(key);
            if (!_counters.TryGetValue((name, hour), out var bucket)) _counters[(name, hour)] = bucket = new();
            bucket[counterName] = bucket.GetValueOrDefault(counterName) + value;
        }
        return Task.CompletedTask;
    }

    public Task<JobCounterStats> GetCounterStatsAsync(string name, TimeSpan? window = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var duration = window ?? TimeSpan.FromHours(24);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(duration, TimeSpan.FromHours(48));
        lock (_lock)
        {
            var now = _timeProvider.GetUtcNow();
            var totals = new Dictionary<string, long>();
            var buckets = new List<JobCounterBucket>();
            for (var hour = TruncateHour(now - duration); hour <= TruncateHour(now); hour = hour.AddHours(1))
            {
                var values = _counters.TryGetValue((name, hour), out var bucket) ? new Dictionary<string, long>(bucket) : new();
                foreach (var pair in values) totals[pair.Key] = totals.GetValueOrDefault(pair.Key) + pair.Value;
                buckets.Add(new() { Hour = hour, Counters = values });
            }
            return Task.FromResult(new JobCounterStats { Totals = totals, Buckets = buckets });
        }
    }

    private static DateTimeOffset TruncateHour(DateTimeOffset value) => new(value.Year, value.Month, value.Day, value.Hour, 0, 0, TimeSpan.Zero);
}
