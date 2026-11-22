using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Jobs.Store;

/// <summary>A process-local job store for development, tests and single-instance apps. Jobs are lost when the process stops.</summary>
/// <remarks>
/// Claims, counts and query pages use indexes, so they do not slow down as retained jobs pile up: per-name sets of the
/// queued and running jobs ordered by when they are due, a set of the finished jobs ordered by when their retention
/// runs out, and sets ordered by creation for each combination of state and name filters. A due job whose concurrency
/// key another job holds is moved aside, to a set of that key's waiting jobs, until the key is released.
/// </remarks>
public sealed class InMemoryJobStore : IJobStore
{
    private readonly Dictionary<string, Entry> _jobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SortedSet<Entry>> _dueByName = new(StringComparer.Ordinal);
    private readonly SortedSet<Entry> _expiring = new(ExpiryComparer.Instance);
    private readonly Dictionary<(JobState? State, string? Name), SortedSet<Entry>> _indexes = new();
    private readonly Dictionary<(string Name, string Key), Entry> _keyHolders = new();
    private readonly Dictionary<(string Name, string Key), SortedSet<Entry>> _waiting = new();
    private readonly Dictionary<(string Name, string Key), Entry> _pending = new();
    private readonly TimeProvider _timeProvider;
    private readonly object _lock = new();
    private long _sequence;

    /// <summary>Creates an empty store that reads "now" from <paramref name="timeProvider"/>, or the system clock when null.</summary>
    public InMemoryJobStore(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    Task<string?> IJobStore.CreateAsync(JobInfo job, CancellationToken cancellationToken) => CreateAsync(job, null);

    /// <inheritdoc />
    Task<string?> IJobStore.CreateAsync(JobInfo job, TimeSpan availableAfter, CancellationToken cancellationToken) => CreateAsync(job, (TimeSpan?)availableAfter);

    private Task<string?> CreateAsync(JobInfo job, TimeSpan? availableAfter)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentException.ThrowIfNullOrWhiteSpace(job.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(job.Name);

        lock (_lock)
        {
            var now = RemoveExpired();
            if (_jobs.ContainsKey(job.Id))
                return Task.FromResult<string?>(null);

            if (job.DeduplicationKey is { } deduplicationKey && _pending.TryGetValue((job.Name, deduplicationKey), out var pending))
                return Task.FromResult<string?>(pending.Id);

            if (availableAfter is { } after)
                job = job with { AvailableAt = now.Add(after) };

            var entry = new Entry(job with
            {
                State = JobState.Queued,
                Attempt = 0,
                UpdatedAt = now,
                ScheduledFor = job.ScheduledFor == default ? job.AvailableAt : job.ScheduledFor,
                ClaimToken = null,
                LeaseExpiresAt = null,
                WorkerId = null,
                Headers = job.Headers is { Count: > 0 } headers ? new Dictionary<string, string>(headers, StringComparer.Ordinal) : ReadOnlyDictionary<string, string>.Empty
            }, ++_sequence);

            _jobs.Add(job.Id, entry);
            Index(entry);
            Schedule(entry, entry.Job.AvailableAt);
            if (job.DeduplicationKey is { } key)
                _pending.Add((job.Name, key), entry);

            return Task.FromResult<string?>(job.Id);
        }
    }

    /// <inheritdoc />
    Task<JobInfo?> IJobStore.GetAsync(string jobId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        lock (_lock)
        {
            RemoveExpired();
            return Task.FromResult(_jobs.TryGetValue(jobId, out var entry) ? entry.Job : null);
        }
    }

    /// <inheritdoc />
    Task<JobPage<JobInfo>> IJobStore.QueryAsync(JobQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();
        var after = ParseCursor(query.ContinuationToken);

        lock (_lock)
        {
            RemoveExpired();
            if (!_indexes.TryGetValue((query.State, query.Name), out var index))
                return Task.FromResult(new JobPage<JobInfo>([], null));

            IEnumerable<Entry> jobs = index;
            if (after is { } cursor)
            {
                var start = Entry.CreationKey(cursor.Ticks, cursor.Id);
                jobs = index.GetViewBetween(start, Entry.LastCreationKey).SkipWhile(e => CreationComparer.Instance.Compare(e, start) == 0);
            }

            var page = jobs.Take(query.Limit + 1).Select(e => e.Job).ToList();
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

    /// <inheritdoc />
    Task<long> IJobStore.CountAsync(JobState? state, string? name, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            RemoveExpired();
            return Task.FromResult(_indexes.TryGetValue((state, name), out var index) ? (long)index.Count : 0);
        }
    }

    /// <summary>
    /// When set, only jobs whose names it accepts are claimed; the others stay queued. Used by <c>FoundatioTestHarness</c>
    /// to pause the job worker.
    /// </summary>
    internal Func<string, bool>? ClaimFilter { get; set; }

    /// <summary>
    /// When set, called before each claim; returning false claims nothing. Used by <c>FoundatioTestHarness</c> to run a
    /// given number of jobs.
    /// </summary>
    internal Func<bool>? ClaimGate { get; set; }

    /// <summary>Counts the queued jobs named <paramref name="name"/> that are due now, those waiting for their concurrency key included.</summary>
    internal int CountDue(string name)
    {
        lock (_lock)
        {
            long now = RemoveExpired().UtcTicks;
            int count = _dueByName.TryGetValue(name, out var due) ? due.Count(e => e.Job.State == JobState.Queued && e.DueTicks <= now) : 0;
            foreach (var (key, waiting) in _waiting)
            {
                if (key.Name == name)
                    count += waiting.Count;
            }

            return count;
        }
    }

    /// <inheritdoc />
    Task<JobInfo?> IJobStore.ClaimAsync(JobClaimRequest request, CancellationToken cancellationToken)
    {
        ValidateClaim(request);
        lock (_lock)
            return Task.FromResult(ClaimNextLocked(request, RemoveExpired()));
    }

    /// <inheritdoc />
    Task<IReadOnlyList<JobInfo>> IJobStore.ClaimBatchAsync(JobClaimRequest request, int maxCount, CancellationToken cancellationToken)
    {
        ValidateClaim(request);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);
        lock (_lock)
        {
            var now = RemoveExpired();
            List<JobInfo>? claimed = null;
            while ((claimed?.Count ?? 0) < maxCount && ClaimNextLocked(request, now) is { } job)
                (claimed ??= new List<JobInfo>(maxCount)).Add(job);

            return Task.FromResult<IReadOnlyList<JobInfo>>(claimed ?? (IReadOnlyList<JobInfo>)[]);
        }
    }

    private static void ValidateClaim(JobClaimRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Names, nameof(request));
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkerId, nameof(request));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(request.LeaseDuration, TimeSpan.Zero, nameof(request));
    }

    private JobInfo? ClaimNextLocked(JobClaimRequest request, DateTimeOffset now)
    {
        Entry? next = null;
        var claimFilter = ClaimFilter;
        foreach (string name in request.Names)
        {
            if (claimFilter is not null && !claimFilter(name))
                continue;

            if (FirstClaimable(name, now.UtcTicks) is { } first && (next is null || DueComparer.Instance.Compare(first, next) < 0))
                next = first;
        }

        if (next is null || ClaimGate?.Invoke() == false)
            return null;

        if (next.Job.ConcurrencyKey is { } concurrencyKey)
            _keyHolders[(next.Job.Name, concurrencyKey)] = next;
        if (next.Job.DeduplicationKey is { } deduplicationKey && _pending.TryGetValue((next.Job.Name, deduplicationKey), out var pending) && pending == next)
            _pending.Remove((next.Job.Name, deduplicationKey));

        var job = next.Job with
        {
            State = JobState.Running,
            Attempt = next.Job.Attempt + 1,
            ClaimToken = Guid.NewGuid().ToString("N"),
            LeaseExpiresAt = now.Add(request.LeaseDuration),
            WorkerId = request.WorkerId,
            StartedAt = next.Job.StartedAt ?? now,
            UpdatedAt = now
        };

        Unschedule(next);
        Update(next, job);
        Schedule(next, job.LeaseExpiresAt.Value);
        return job;
    }

    /// <summary>
    /// The job of <paramref name="name"/> due longest, moving aside the due jobs ahead of it whose concurrency key
    /// another job holds, so later claims don't pass over them again.
    /// </summary>
    private Entry? FirstClaimable(string name, long nowTicks)
    {
        while (_dueByName.TryGetValue(name, out var due) && due.Min is { } first && first.DueTicks <= nowTicks)
        {
            if (first.Job is not { State: JobState.Queued, ConcurrencyKey: { } key } || !_keyHolders.TryGetValue((name, key), out var holder) || holder == first)
                return first;

            Unschedule(first);
            if (!_waiting.TryGetValue((name, key), out var waiting))
                _waiting.Add((name, key), waiting = new SortedSet<Entry>(DueComparer.Instance));

            waiting.Add(first);
            first.IsWaiting = true;
        }

        return null;
    }

    /// <summary>Releases the job's concurrency key if it holds it, and makes the job waiting longest for the key claimable.</summary>
    private void ReleaseConcurrencyKey(Entry entry)
    {
        if (entry.Job.ConcurrencyKey is not { } key || !_keyHolders.TryGetValue((entry.Job.Name, key), out var holder) || holder != entry)
            return;

        _keyHolders.Remove((entry.Job.Name, key));
        ScheduleNextWaiting(entry.Job.Name, key);
    }

    private void ScheduleNextWaiting(string name, string key)
    {
        if (!_waiting.TryGetValue((name, key), out var waiting) || waiting.Min is not { } next)
            return;

        RemoveWaiting(next);
        Schedule(next, new DateTimeOffset(next.DueTicks, TimeSpan.Zero));
    }

    private void RemoveWaiting(Entry entry)
    {
        if (!entry.IsWaiting)
            return;

        var key = (entry.Job.Name, entry.Job.ConcurrencyKey!);
        if (_waiting.TryGetValue(key, out var waiting) && waiting.Remove(entry) && waiting.Count == 0)
            _waiting.Remove(key);

        entry.IsWaiting = false;
    }

    /// <summary>
    /// Forgets a queued job that leaves without being claimed: it no longer deduplicates, and if it was waiting for its
    /// concurrency key, or is the one a released key made claimable, the next job waiting for the key gets its turn.
    /// </summary>
    private void ReleaseQueued(Entry entry)
    {
        if (entry.Job.DeduplicationKey is { } deduplicationKey && _pending.TryGetValue((entry.Job.Name, deduplicationKey), out var pending) && pending == entry)
            _pending.Remove((entry.Job.Name, deduplicationKey));

        if (entry.Job.ConcurrencyKey is not { } key)
            return;

        RemoveWaiting(entry);
        if (!_keyHolders.ContainsKey((entry.Job.Name, key)))
            ScheduleNextWaiting(entry.Job.Name, key);
    }

    /// <inheritdoc />
    Task<JobLeaseStatus> IJobStore.RenewAsync(string jobId, string claimToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(claimToken);
        lock (_lock)
        {
            var now = RemoveExpired();
            var entry = FindClaimed(jobId, claimToken);
            if (entry is null)
                return Task.FromResult(JobLeaseStatus.Lost);

            Unschedule(entry);
            entry.Job = entry.Job with { LeaseExpiresAt = now.Add(leaseDuration), UpdatedAt = now };
            Schedule(entry, entry.Job.LeaseExpiresAt.Value);
            return Task.FromResult(entry.Job.CancellationRequested ? JobLeaseStatus.CancellationRequested : JobLeaseStatus.Renewed);
        }
    }

    /// <inheritdoc />
    Task<bool> IJobStore.ReportProgressAsync(string jobId, string claimToken, int? percent, string? message, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(claimToken);
        lock (_lock)
        {
            var now = RemoveExpired();
            var entry = FindClaimed(jobId, claimToken);
            if (entry is null)
                return Task.FromResult(false);

            entry.Job = entry.Job with { Progress = percent ?? entry.Job.Progress, Message = message ?? entry.Job.Message, UpdatedAt = now };
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    Task<bool> IJobStore.CompleteAsync(string jobId, string claimToken, JobCompletion completion, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(claimToken);
        ArgumentNullException.ThrowIfNull(completion);
        if (completion.State == JobState.Running)
            throw new ArgumentException("A run cannot complete as Running.", nameof(completion));

        lock (_lock)
        {
            var now = RemoveExpired();
            var entry = FindClaimed(jobId, claimToken);
            if (entry is null)
                return Task.FromResult(false);

            Unschedule(entry);
            ReleaseConcurrencyKey(entry);
            var job = entry.Job;
            if (completion.State == JobState.Queued)
            {
                Update(entry, job with
                {
                    ClaimToken = null,
                    LeaseExpiresAt = null,
                    WorkerId = null,
                    UpdatedAt = now,
                    State = completion.State,
                    AvailableAt = now.Add(completion.RetryDelay),
                    Attempt = completion.RefundAttempt ? Math.Max(0, job.Attempt - 1) : job.Attempt,
                    Error = completion.Error ?? job.Error
                });
                Schedule(entry, entry.Job.AvailableAt);
            }
            else
            {
                bool succeeded = completion.State == JobState.Succeeded;
                Update(entry, job with
                {
                    ClaimToken = null,
                    LeaseExpiresAt = null,
                    WorkerId = null,
                    UpdatedAt = now,
                    State = completion.State,
                    CompletedAt = now,
                    Message = completion.Message ?? job.Message,
                    Error = completion.Error ?? (succeeded ? null : job.Error),
                    Payload = succeeded ? ReadOnlyMemory<byte>.Empty : job.Payload
                });
                Expire(entry, now.Add(completion.RetainFor));
            }

            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    Task<bool> IJobStore.RequestCancellationAsync(string jobId, TimeSpan retainFor, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        lock (_lock)
        {
            var now = RemoveExpired();
            if (!_jobs.TryGetValue(jobId, out var entry) || entry.Job.State.IsTerminal())
                return Task.FromResult(false);

            if (entry.Job.State == JobState.Queued)
            {
                Unschedule(entry);
                ReleaseQueued(entry);
                Update(entry, entry.Job with { State = JobState.Cancelled, CancellationRequested = true, CompletedAt = now, UpdatedAt = now });
                Expire(entry, now.Add(retainFor));
            }
            else
            {
                entry.Job = entry.Job with { CancellationRequested = true, UpdatedAt = now };
            }

            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    Task<bool> IJobStore.DeleteAsync(string jobId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        lock (_lock)
        {
            RemoveExpired();
            if (!_jobs.TryGetValue(jobId, out var entry))
                return Task.FromResult(false);

            Remove(entry);
            return Task.FromResult(true);
        }
    }

    /// <summary>Reads the clock, and first removes the jobs whose retention has run out so no operation sees them.</summary>
    private DateTimeOffset RemoveExpired()
    {
        var now = _timeProvider.GetUtcNow();
        while (_expiring.Min is { } expired && expired.ExpiresAt <= now)
            Remove(expired);

        return now;
    }

    private Entry? FindClaimed(string jobId, string claimToken)
    {
        return _jobs.TryGetValue(jobId, out var entry) && entry.Job is { State: JobState.Running } job && job.ClaimToken == claimToken ? entry : null;
    }

    /// <summary>Replaces the job, moving it to the indexes of its new state.</summary>
    private void Update(Entry entry, JobInfo job)
    {
        if (job.State == entry.Job.State)
        {
            entry.Job = job;
            return;
        }

        // The indexes of all jobs and of the job's name do not depend on its state, so only the state's indexes change.
        RemoveFromIndex((entry.Job.State, null), entry);
        RemoveFromIndex((entry.Job.State, entry.Job.Name), entry);
        entry.Job = job;
        AddToIndex((job.State, null), entry);
        AddToIndex((job.State, job.Name), entry);
    }

    private void Remove(Entry entry)
    {
        Unschedule(entry);
        if (entry.Job.State == JobState.Queued)
            ReleaseQueued(entry);
        else if (entry.Job.State == JobState.Running)
            ReleaseConcurrencyKey(entry);

        if (entry.ExpiresAt is not null)
            _expiring.Remove(entry);

        Unindex(entry);
        _jobs.Remove(entry.Id);
    }

    /// <summary>Makes a queued or running job claimable once <paramref name="due"/> has passed.</summary>
    private void Schedule(Entry entry, DateTimeOffset due)
    {
        entry.DueTicks = due.UtcTicks;
        if (!_dueByName.TryGetValue(entry.Job.Name, out var set))
            _dueByName.Add(entry.Job.Name, set = new SortedSet<Entry>(DueComparer.Instance));

        set.Add(entry);
        entry.IsScheduled = true;
    }

    private void Unschedule(Entry entry)
    {
        if (!entry.IsScheduled)
            return;

        if (_dueByName.TryGetValue(entry.Job.Name, out var set) && set.Remove(entry) && set.Count == 0)
            _dueByName.Remove(entry.Job.Name);

        entry.IsScheduled = false;
    }

    private void Expire(Entry entry, DateTimeOffset expiresAt)
    {
        entry.ExpiresAt = expiresAt;
        _expiring.Add(entry);
    }

    private void Index(Entry entry)
    {
        AddToIndex((null, null), entry);
        AddToIndex((null, entry.Job.Name), entry);
        AddToIndex((entry.Job.State, null), entry);
        AddToIndex((entry.Job.State, entry.Job.Name), entry);
    }

    private void Unindex(Entry entry)
    {
        RemoveFromIndex((null, null), entry);
        RemoveFromIndex((null, entry.Job.Name), entry);
        RemoveFromIndex((entry.Job.State, null), entry);
        RemoveFromIndex((entry.Job.State, entry.Job.Name), entry);
    }

    private void AddToIndex((JobState? State, string? Name) key, Entry entry)
    {
        if (!_indexes.TryGetValue(key, out var index))
            _indexes.Add(key, index = new SortedSet<Entry>(CreationComparer.Instance));

        index.Add(entry);
    }

    private void RemoveFromIndex((JobState? State, string? Name) key, Entry entry)
    {
        if (_indexes.TryGetValue(key, out var index) && index.Remove(entry) && index.Count == 0)
            _indexes.Remove(key);
    }

    private sealed class Entry
    {
        private static readonly JobInfo Placeholder = new() { Id = "-", Name = "-" };

        public Entry(JobInfo job, long sequence)
        {
            Job = job;
            Id = job.Id;
            CreatedTicks = job.CreatedAt.UtcTicks;
            Sequence = sequence;
        }

        private Entry(long createdTicks, string id)
        {
            Job = Placeholder;
            Id = id;
            CreatedTicks = createdTicks;
        }

        /// <summary>Sorts after every job in a creation index, so it bounds a page's range.</summary>
        public static Entry LastCreationKey { get; } = new(Int64.MinValue, String.Empty);

        public JobInfo Job { get; set; }

        public string Id { get; }

        public long CreatedTicks { get; }

        /// <summary>Breaks ties between jobs due at the same time, oldest first.</summary>
        public long Sequence { get; }

        /// <summary>When a queued job becomes available, or a running job's lease runs out.</summary>
        public long DueTicks { get; set; }

        public bool IsScheduled { get; set; }

        /// <summary>Whether the job is due but set aside until its concurrency key is released.</summary>
        public bool IsWaiting { get; set; }

        public DateTimeOffset? ExpiresAt { get; set; }

        /// <summary>An entry that sorts where a job created at <paramref name="createdTicks"/> with <paramref name="id"/> would.</summary>
        public static Entry CreationKey(long createdTicks, string id) => new(createdTicks, id);
    }

    private sealed class DueComparer : IComparer<Entry>
    {
        public static readonly DueComparer Instance = new();

        public int Compare(Entry? x, Entry? y)
        {
            if (x is null || y is null)
                return x is null ? (y is null ? 0 : -1) : 1;

            int result = x.DueTicks.CompareTo(y.DueTicks);
            return result != 0 ? result : x.Sequence.CompareTo(y.Sequence);
        }
    }

    private sealed class ExpiryComparer : IComparer<Entry>
    {
        public static readonly ExpiryComparer Instance = new();

        public int Compare(Entry? x, Entry? y)
        {
            if (x is null || y is null)
                return x is null ? (y is null ? 0 : -1) : 1;

            int result = Nullable.Compare(x.ExpiresAt, y.ExpiresAt);
            return result != 0 ? result : x.Sequence.CompareTo(y.Sequence);
        }
    }

    /// <summary>Newest first, by creation time and then id, the order queries return.</summary>
    private sealed class CreationComparer : IComparer<Entry>
    {
        public static readonly CreationComparer Instance = new();

        public int Compare(Entry? x, Entry? y)
        {
            if (x is null || y is null)
                return x is null ? (y is null ? 0 : -1) : 1;

            int result = y.CreatedTicks.CompareTo(x.CreatedTicks);
            return result != 0 ? result : String.CompareOrdinal(y.Id, x.Id);
        }
    }
}
