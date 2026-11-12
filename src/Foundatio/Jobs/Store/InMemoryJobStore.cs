using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Jobs.Store;

/// <summary>A process-local job store for development, tests and single-instance apps. Jobs are lost when the process stops.</summary>
public sealed class InMemoryJobStore : IJobStore
{
    private readonly Dictionary<string, Entry> _jobs = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly object _lock = new();
    private DateTimeOffset _nextCleanup;

    public InMemoryJobStore(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<bool> CreateAsync(JobInfo job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentException.ThrowIfNullOrWhiteSpace(job.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(job.Name);

        lock (_lock)
        {
            var now = Now();
            if (_jobs.TryGetValue(job.Id, out var existing) && !existing.IsExpired(now))
                return Task.FromResult(false);

            _jobs[job.Id] = new Entry(job with
            {
                State = JobState.Queued,
                Attempt = 0,
                UpdatedAt = now,
                ClaimToken = null,
                LeaseExpiresAt = null,
                WorkerId = null,
                Headers = job.Headers is { Count: > 0 } headers ? new Dictionary<string, string>(headers, StringComparer.Ordinal) : ReadOnlyDictionary<string, string>.Empty
            });
            return Task.FromResult(true);
        }
    }

    public Task<JobInfo?> GetAsync(string jobId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        lock (_lock)
            return Task.FromResult(Find(jobId, Now())?.Job);
    }

    public Task<JobPage<JobInfo>> QueryAsync(JobQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();
        var after = ParseCursor(query.ContinuationToken);

        lock (_lock)
        {
            var now = Now();
            var page = Matching(query.State, query.Name, now)
                .Where(j => after is null || j.CreatedAt.UtcTicks < after.Value.Ticks || j.CreatedAt.UtcTicks == after.Value.Ticks && String.CompareOrdinal(j.Id, after.Value.Id) < 0)
                .OrderByDescending(j => j.CreatedAt.UtcTicks).ThenByDescending(j => j.Id, StringComparer.Ordinal)
                .Take(query.Limit + 1)
                .ToList();

            string? continuation = null;
            if (page.Count > query.Limit)
            {
                page.RemoveAt(page.Count - 1);
                var last = page[^1];
                continuation = $"{last.CreatedAt.UtcTicks.ToString(CultureInfo.InvariantCulture)}|{last.Id}";
            }

            return Task.FromResult(new JobPage<JobInfo>(page, continuation));
        }
    }

    private static (long Ticks, string Id)? ParseCursor(string? token)
    {
        if (String.IsNullOrEmpty(token))
            return null;

        int separator = token.IndexOf('|');
        if (separator <= 0 || !Int64.TryParse(token.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out long ticks))
            throw new ArgumentException("The continuation token is not valid.", nameof(token));

        return (ticks, token[(separator + 1)..]);
    }

    public Task<long> CountAsync(JobState? state = null, string? name = null, CancellationToken cancellationToken = default)
    {
        lock (_lock)
            return Task.FromResult((long)Matching(state, name, Now()).Count());
    }

    public Task<JobInfo?> ClaimAsync(JobClaimRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Names, nameof(request));
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkerId, nameof(request));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(request.LeaseDuration, TimeSpan.Zero, nameof(request));
        lock (_lock)
        {
            var now = Now();
            CleanupExpired(now);

            Entry? next = null;
            DateTimeOffset nextDue = default;
            foreach (var entry in _jobs.Values)
            {
                var job = entry.Job;
                DateTimeOffset due;
                if (job.State == JobState.Queued && job.AvailableAt <= now)
                    due = job.AvailableAt;
                else if (job.State == JobState.Running && job.LeaseExpiresAt <= now)
                    due = job.LeaseExpiresAt.Value;
                else
                    continue;

                if (!request.Names.Contains(job.Name))
                    continue;

                if (next is null || due < nextDue)
                {
                    next = entry;
                    nextDue = due;
                }
            }

            if (next is null)
                return Task.FromResult<JobInfo?>(null);

            next.Job = next.Job with
            {
                State = JobState.Running,
                Attempt = next.Job.Attempt + 1,
                ClaimToken = Guid.NewGuid().ToString("N"),
                LeaseExpiresAt = now.Add(request.LeaseDuration),
                WorkerId = request.WorkerId,
                StartedAt = next.Job.StartedAt ?? now,
                UpdatedAt = now
            };

            return Task.FromResult<JobInfo?>(next.Job);
        }
    }

    public Task<JobLeaseStatus> RenewAsync(string jobId, string claimToken, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(claimToken);
        lock (_lock)
        {
            var now = Now();
            var entry = FindClaimed(jobId, claimToken, now);
            if (entry is null)
                return Task.FromResult(JobLeaseStatus.Lost);

            entry.Job = entry.Job with { LeaseExpiresAt = now.Add(leaseDuration), UpdatedAt = now };
            return Task.FromResult(entry.Job.CancellationRequested ? JobLeaseStatus.CancellationRequested : JobLeaseStatus.Renewed);
        }
    }

    public Task<bool> ReportProgressAsync(string jobId, string claimToken, int? percent, string? message, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(claimToken);
        lock (_lock)
        {
            var now = Now();
            var entry = FindClaimed(jobId, claimToken, now);
            if (entry is null)
                return Task.FromResult(false);

            entry.Job = entry.Job with { Progress = percent ?? entry.Job.Progress, Message = message ?? entry.Job.Message, UpdatedAt = now };
            return Task.FromResult(true);
        }
    }

    public Task<bool> CompleteAsync(string jobId, string claimToken, JobCompletion completion, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(claimToken);
        ArgumentNullException.ThrowIfNull(completion);
        if (completion.State == JobState.Running)
            throw new ArgumentException("A run cannot complete as Running.", nameof(completion));

        lock (_lock)
        {
            var now = Now();
            var entry = FindClaimed(jobId, claimToken, now);
            if (entry is null)
                return Task.FromResult(false);

            var job = entry.Job with { ClaimToken = null, LeaseExpiresAt = null, WorkerId = null, UpdatedAt = now, State = completion.State };
            if (completion.State == JobState.Queued)
            {
                entry.Job = job with
                {
                    AvailableAt = now.Add(completion.RetryDelay),
                    Attempt = completion.RefundAttempt ? Math.Max(0, job.Attempt - 1) : job.Attempt,
                    Error = completion.Error ?? job.Error
                };
            }
            else
            {
                bool succeeded = completion.State == JobState.Succeeded;
                entry.Job = job with
                {
                    CompletedAt = now,
                    Message = completion.Message ?? job.Message,
                    Error = completion.Error ?? (succeeded ? null : job.Error),
                    Payload = succeeded ? ReadOnlyMemory<byte>.Empty : job.Payload
                };
                entry.ExpiresAt = now.Add(completion.RetainFor);
            }

            return Task.FromResult(true);
        }
    }

    public Task<bool> RequestCancellationAsync(string jobId, TimeSpan retainFor, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        lock (_lock)
        {
            var now = Now();
            var entry = Find(jobId, now);
            if (entry is null || entry.Job.State.IsTerminal())
                return Task.FromResult(false);

            if (entry.Job.State == JobState.Queued)
            {
                entry.Job = entry.Job with { State = JobState.Cancelled, CancellationRequested = true, CompletedAt = now, UpdatedAt = now };
                entry.ExpiresAt = now.Add(retainFor);
            }
            else
            {
                entry.Job = entry.Job with { CancellationRequested = true, UpdatedAt = now };
            }

            return Task.FromResult(true);
        }
    }

    public Task<bool> DeleteAsync(string jobId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        lock (_lock)
            return Task.FromResult(_jobs.Remove(jobId));
    }

    private DateTimeOffset Now() => _timeProvider.GetUtcNow();

    private Entry? Find(string jobId, DateTimeOffset now)
    {
        return _jobs.TryGetValue(jobId, out var entry) && !entry.IsExpired(now) ? entry : null;
    }

    private Entry? FindClaimed(string jobId, string claimToken, DateTimeOffset now)
    {
        var entry = Find(jobId, now);
        return entry is { Job.State: JobState.Running } && entry.Job.ClaimToken == claimToken ? entry : null;
    }

    private IEnumerable<JobInfo> Matching(JobState? status, string? name, DateTimeOffset now)
    {
        return _jobs.Values.Where(e => !e.IsExpired(now)).Select(e => e.Job)
            .Where(j => (status is null || j.State == status) && (name is null || j.Name == name));
    }

    private void CleanupExpired(DateTimeOffset now)
    {
        if (now < _nextCleanup)
            return;

        _nextCleanup = now.AddSeconds(30);
        foreach (string id in _jobs.Where(j => j.Value.IsExpired(now)).Select(j => j.Key).ToList())
            _jobs.Remove(id);
    }

    private sealed class Entry
    {
        public Entry(JobInfo job)
        {
            Job = job;
        }

        public JobInfo Job { get; set; }
        public DateTimeOffset? ExpiresAt { get; set; }

        public bool IsExpired(DateTimeOffset now) => ExpiresAt <= now;
    }
}
