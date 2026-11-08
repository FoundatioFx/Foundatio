using System;
using System.Collections.Generic;
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

            _jobs[job.Id] = new Entry(job with { Status = JobStatus.Queued, Attempt = 0, UpdatedAt = now, ClaimToken = null, LeaseExpiresAt = null, WorkerId = null });
            return Task.FromResult(true);
        }
    }

    public Task<JobInfo?> GetAsync(string jobId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        lock (_lock)
            return Task.FromResult(Find(jobId, Now())?.Job);
    }

    public Task<JobPage> QueryAsync(JobQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();
        var after = ParseCursor(query.ContinuationToken);

        lock (_lock)
        {
            var now = Now();
            var page = Matching(query.Status, query.Name, now)
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

            return Task.FromResult(new JobPage(page, continuation));
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

    public Task<long> CountAsync(JobStatus? status = null, string? name = null, CancellationToken cancellationToken = default)
    {
        lock (_lock)
            return Task.FromResult((long)Matching(status, name, Now()).Count());
    }

    public Task<JobInfo?> ClaimAsync(JobClaimRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
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
                if (job.Status == JobStatus.Queued && job.AvailableAt <= now)
                    due = job.AvailableAt;
                else if (job.Status == JobStatus.Running && job.LeaseExpiresAt <= now)
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
                Status = JobStatus.Running,
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
        ArgumentNullException.ThrowIfNull(completion);
        lock (_lock)
        {
            var now = Now();
            var entry = FindClaimed(jobId, claimToken, now);
            if (entry is null)
                return Task.FromResult(false);

            var job = entry.Job with { ClaimToken = null, LeaseExpiresAt = null, WorkerId = null, UpdatedAt = now, Status = completion.Status };
            if (completion.Status == JobStatus.Queued)
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
                entry.Job = job with { CompletedAt = now, Message = completion.Message ?? job.Message, Error = completion.Error ?? (completion.Status == JobStatus.Succeeded ? null : job.Error) };
                entry.ExpiresAt = now.Add(completion.RetainFor);
            }

            return Task.FromResult(true);
        }
    }

    public Task<bool> RequestCancellationAsync(string jobId, TimeSpan retainFor, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var now = Now();
            var entry = Find(jobId, now);
            if (entry is null || entry.Job.Status.IsTerminal())
                return Task.FromResult(false);

            if (entry.Job.Status == JobStatus.Queued)
            {
                entry.Job = entry.Job with { Status = JobStatus.Cancelled, CancellationRequested = true, CompletedAt = now, UpdatedAt = now };
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
        return entry is { Job.Status: JobStatus.Running } && entry.Job.ClaimToken == claimToken ? entry : null;
    }

    private IEnumerable<JobInfo> Matching(JobStatus? status, string? name, DateTimeOffset now)
    {
        return _jobs.Values.Where(e => !e.IsExpired(now)).Select(e => e.Job)
            .Where(j => (status is null || j.Status == status) && (name is null || j.Name == name));
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
