using Foundatio.Jobs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;

namespace Foundatio.Messaging;

/// <summary>
/// In-memory implementation of <see cref="IMessageExecutionStore"/>.
/// Suitable for development, testing, and single-node deployments.
/// Expired entries are lazily cleaned up on access.
/// </summary>
public sealed class InMemoryMessageExecutionStore : IMessageExecutionStore
{
    /// <inheritdoc />
    public bool IsShared => false;

    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, JobEntry> _jobs = new();
    private readonly ConcurrentDictionary<string, bool> _cancellations = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, long>> _counterBuckets = new();
    private readonly TimeProvider _timeProvider;
    private int _accessCount;

    public InMemoryMessageExecutionStore(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task SetJobStateAsync(MessageExecutionState state, TimeSpan? expiry = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            var expiresAt = expiry.HasValue ? now + expiry.Value : DateTimeOffset.MaxValue;

            _cancellations.TryRemove(state.JobId, out _);
            _jobs[state.JobId] = new JobEntry(state, expiresAt, expiry);

            CleanupIfNeeded();

            return Task.CompletedTask;

        }
    }

    public Task<MessageExecutionState?> GetJobStateAsync(string jobId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out var entry))
                return Task.FromResult<MessageExecutionState?>(null);

            if (!IsExpired(entry))
                return Task.FromResult<MessageExecutionState?>(entry.State);

            // Remove expired entry on access
            _jobs.TryRemove(jobId, out _);
            _cancellations.TryRemove(jobId, out _);

            return Task.FromResult<MessageExecutionState?>(null);

        }
    }

    public Task<bool> UpdateJobStatusAsync(string jobId, MessageExecutionStatus status, DateTimeOffset? startedUtc = null, DateTimeOffset? completedUtc = null, string? errorMessage = null, int? progress = null, int? attempt = null, TimeSpan? expiry = null, CancellationToken cancellationToken = default, string? workerId = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out var entry) || IsExpired(entry)
                || IsTerminal(entry.State.Status) || attempt < entry.State.Attempt
                || (status == MessageExecutionStatus.Processing && attempt == entry.State.Attempt && entry.State.Status is MessageExecutionStatus.Processing or MessageExecutionStatus.RetryPending)
                || (status == MessageExecutionStatus.EnqueueUnknown && entry.State.Status != MessageExecutionStatus.Queued))
                return Task.FromResult(false);

            var now = _timeProvider.GetUtcNow();
            var updated = entry.State with
            {
                Status = status,
                StartedUtc = startedUtc ?? entry.State.StartedUtc,
                CompletedUtc = IsTerminal(status) ? completedUtc ?? now : null,
                ErrorMessage = errorMessage ?? (status == MessageExecutionStatus.Processing ? null : entry.State.ErrorMessage),
                Progress = progress ?? (status == MessageExecutionStatus.Processing ? 0 : entry.State.Progress),
                ProgressMessage = status == MessageExecutionStatus.Processing ? null : entry.State.ProgressMessage,
                LastHeartbeatUtc = status == MessageExecutionStatus.Processing ? startedUtc ?? now : entry.State.LastHeartbeatUtc,
                WorkerId = workerId ?? entry.State.WorkerId,
                Attempt = attempt ?? entry.State.Attempt,
                LastUpdatedUtc = now
            };
            _jobs[jobId] = new JobEntry(updated, expiry.HasValue ? now + expiry.Value : entry.ExpiresAt, expiry ?? entry.Retention);
            return Task.FromResult(true);
        }
    }

    public Task UpdateJobProgressAsync(string jobId, int progress, string? progressMessage = null, TimeSpan? expiry = null, CancellationToken cancellationToken = default, int? expectedAttempt = null)
        => UpdateLiveJobAsync(jobId, expectedAttempt, expiry, state => state with
        {
            Progress = progress,
            ProgressMessage = progressMessage,
            LastUpdatedUtc = _timeProvider.GetUtcNow()
        }, cancellationToken);

    public Task HeartbeatAsync(string jobId, CancellationToken cancellationToken = default, int? expectedAttempt = null, TimeSpan? expiry = null)
        => UpdateLiveJobAsync(jobId, expectedAttempt, expiry, state => state with
        {
            LastHeartbeatUtc = _timeProvider.GetUtcNow(),
            LastUpdatedUtc = _timeProvider.GetUtcNow()
        }, cancellationToken);

    private Task UpdateLiveJobAsync(string jobId, int? expectedAttempt, TimeSpan? expiry, Func<MessageExecutionState, MessageExecutionState> update, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_jobs.TryGetValue(jobId, out var entry) && !IsExpired(entry) && entry.State.Status == MessageExecutionStatus.Processing
                && (!expectedAttempt.HasValue || expectedAttempt == entry.State.Attempt))
                _jobs[jobId] = new JobEntry(update(entry.State), (expiry ?? entry.Retention) is { } retention ? _timeProvider.GetUtcNow() + retention : entry.ExpiresAt, expiry ?? entry.Retention);
            return Task.CompletedTask;
        }
    }

    private static bool IsTerminal(MessageExecutionStatus status) => status is MessageExecutionStatus.Completed or MessageExecutionStatus.Failed or MessageExecutionStatus.Cancelled;

    public Task<bool> RequestCancellationAsync(string jobId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out var entry) || IsExpired(entry))
                return Task.FromResult(false);

            // Only allow cancellation for non-terminal states
            if (entry.State.Status is MessageExecutionStatus.Completed or MessageExecutionStatus.Failed or MessageExecutionStatus.Cancelled)
                return Task.FromResult(false);

            _cancellations[jobId] = true;
            return Task.FromResult(true);

        }
    }

    public Task<bool> IsCancellationRequestedAsync(string jobId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(_cancellations.ContainsKey(jobId));

        }
    }

    public Task RemoveJobStateAsync(string jobId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _jobs.TryRemove(jobId, out _);
            _cancellations.TryRemove(jobId, out _);
            return Task.CompletedTask;

        }
    }

    public Task IncrementCounterAsync(string queueName, string counterName, long value = 1, CancellationToken cancellationToken = default)
    {
        var bucketKey = GetBucketKey(queueName, _timeProvider.GetUtcNow());
        var bucket = _counterBuckets.GetOrAdd(bucketKey, _ => new ConcurrentDictionary<string, long>());
        bucket.AddOrUpdate(counterName, value, (_, existing) => existing + value);
        return Task.CompletedTask;
    }

    public Task<JobCounterStats> GetCounterStatsAsync(string queueName, TimeSpan? window = null, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var effectiveWindow = window ?? TimeSpan.FromHours(24);
        var startHour = TruncateToHour(now - effectiveWindow);
        var endHour = TruncateToHour(now);

        var totals = new Dictionary<string, long>();
        var buckets = new List<JobCounterBucket>();

        for (var hour = startHour; hour <= endHour; hour = hour.AddHours(1))
        {
            var bucketKey = GetBucketKey(queueName, hour);
            var counters = new Dictionary<string, long>();

            if (_counterBuckets.TryGetValue(bucketKey, out var bucket))
            {
                foreach (var kvp in bucket)
                {
                    counters[kvp.Key] = kvp.Value;
                    totals[kvp.Key] = totals.GetValueOrDefault(kvp.Key) + kvp.Value;
                }
            }

            buckets.Add(new JobCounterBucket { Hour = hour, Counters = counters });
        }

        return Task.FromResult(new JobCounterStats { Totals = totals, Buckets = buckets });
    }

    private static string GetBucketKey(string queueName, DateTimeOffset timestamp)
    {
        var hour = TruncateToHour(timestamp);
        return $"{queueName}:{hour:yyyy-MM-ddTHH}";
    }

    private static DateTimeOffset TruncateToHour(DateTimeOffset timestamp)
        => new(timestamp.Year, timestamp.Month, timestamp.Day, timestamp.Hour, 0, 0, TimeSpan.Zero);

    public Task<IReadOnlyList<MessageExecutionState>> GetJobsByStatusAsync(string queueName, MessageExecutionStatus status, int skip = 0, int take = 50, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var results = _jobs.Values
            .Where(e => !IsExpired(e, now)
                && string.Equals(e.State.QueueName, queueName, StringComparison.OrdinalIgnoreCase)
                && e.State.Status == status)
            .OrderByDescending(e => e.State.CreatedUtc)
            .Skip(skip)
            .Take(take)
            .Select(e => e.State)
            .ToList();

        return Task.FromResult<IReadOnlyList<MessageExecutionState>>(results);
    }

    public Task<long> GetJobCountByStatusAsync(string queueName, MessageExecutionStatus status, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var count = _jobs.Values.Count(e => !IsExpired(e, now)
            && string.Equals(e.State.QueueName, queueName, StringComparison.OrdinalIgnoreCase)
            && e.State.Status == status);

        return Task.FromResult((long)count);
    }

    private bool IsExpired(JobEntry entry) => IsExpired(entry, _timeProvider.GetUtcNow());

    private static bool IsExpired(JobEntry entry, DateTimeOffset now) => now >= entry.ExpiresAt;

    private void CleanupIfNeeded()
    {
        // Run cleanup every 100 writes to avoid accumulating expired entries
        if (Interlocked.Increment(ref _accessCount) % 100 != 0)
            return;

        var now = _timeProvider.GetUtcNow();
        foreach (var kvp in _jobs)
        {
            if (now >= kvp.Value.ExpiresAt)
            {
                _jobs.TryRemove(kvp.Key, out _);
                _cancellations.TryRemove(kvp.Key, out _);
            }
        }
    }

    private sealed record JobEntry(MessageExecutionState State, DateTimeOffset ExpiresAt, TimeSpan? Retention);
}
