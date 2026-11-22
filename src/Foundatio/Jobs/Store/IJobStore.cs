using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Jobs.Store;

/// <summary>
/// The contract a database implements to store Foundatio jobs. Each operation must be atomic.
/// </summary>
/// <remarks>
/// A store only persists state. Retries, backoff, scheduling, concurrency and cancellation are handled by Foundatio.
/// Use the store's own clock for "now" (lease expiry, availability, retention) so workers with skewed clocks agree.
/// Implementations are verified by <c>JobStoreTestsBase</c> in Foundatio.TestHarness.
/// </remarks>
[Experimental(ProviderApi.DiagnosticId, UrlFormat = ProviderApi.UrlFormat)]
public interface IJobStore
{
    /// <summary>
    /// Adds a new job as <see cref="JobState.Queued"/> with <see cref="JobInfo.Attempt"/> 0 and no claim, keeping the
    /// other fields as given, including every <see cref="JobInfo.Headers"/> entry exactly (names are case-sensitive and
    /// values may be empty); every read of the job returns them. <see cref="JobInfo.CreatedAt"/>,
    /// <see cref="JobInfo.AvailableAt"/> and <see cref="JobInfo.ScheduledFor"/> come from the caller's clock. When
    /// <see cref="JobInfo.ScheduledFor"/> is <c>default</c>, the store sets it to <see cref="JobInfo.AvailableAt"/>; no
    /// later operation changes it.
    /// </summary>
    /// <returns>
    /// The job's id when it was created. Null, changing nothing, when a job with the same id exists (including a
    /// finished job that is still retained). When <see cref="JobInfo.DeduplicationKey"/> is set and another job with the
    /// same name and key has not been claimed, cancelled or deleted since it was created, that job's id, creating nothing.
    /// The id check comes first. Both checks and the creation are one atomic step, so concurrent creates with one key
    /// create one job.
    /// </returns>
    Task<string?> CreateAsync(JobInfo job, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a new job as <see cref="CreateAsync(JobInfo, CancellationToken)"/> does, except that it becomes available
    /// <paramref name="availableAfter"/> after now on the store's own clock instead of at <see cref="JobInfo.AvailableAt"/>,
    /// which comes from the caller's clock; when <see cref="JobInfo.ScheduledFor"/> is <c>default</c>, it is set to that
    /// time too. Clients use this for immediate and delayed jobs, so a producer whose clock is ahead of the store's
    /// doesn't hold its jobs back, and one behind it doesn't run them early. The default creates the job at
    /// <see cref="JobInfo.AvailableAt"/>, for stores that have no clock of their own.
    /// </summary>
    /// <returns>As <see cref="CreateAsync(JobInfo, CancellationToken)"/> returns.</returns>
    Task<string?> CreateAsync(JobInfo job, TimeSpan availableAfter, CancellationToken cancellationToken = default) => CreateAsync(job, cancellationToken);

    /// <summary>Returns the job, or null when it does not exist or its retention has passed.</summary>
    Task<JobInfo?> GetAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists jobs newest first (by <see cref="JobInfo.CreatedAt"/>, then id) matching the query's status and name.
    /// Expired jobs are excluded. Call <see cref="JobQuery.Validate"/> first.
    /// </summary>
    Task<JobPage<JobInfo>> QueryAsync(JobQuery query, CancellationToken cancellationToken = default);

    /// <summary>Counts jobs matching the status and name. Jobs past their retention should not be counted.</summary>
    Task<long> CountAsync(JobState? state = null, string? name = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically takes the job that has been due longest among <see cref="JobClaimRequest.Names"/>: a queued job whose
    /// <see cref="JobInfo.AvailableAt"/> has passed, or a running job whose lease expired. Marks it running, increments
    /// <see cref="JobInfo.Attempt"/>, assigns a new <see cref="JobInfo.ClaimToken"/> and lease, and returns it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A job's <see cref="JobInfo.ConcurrencyKey"/> is held, for its name, from when the job is claimed until it leaves
    /// <see cref="JobState.Running"/> (any <see cref="CompleteAsync"/>, a retry or release included) or is deleted. A
    /// running job whose lease expired keeps its key until it is claimed again. A queued job whose key another job holds
    /// is skipped: it stays queued with its attempt unchanged, and is claimable once the key is released. Skipped jobs
    /// must not keep other jobs from being claimed, so a store moves them aside (or indexes them by key) rather than
    /// scanning past them on every claim.
    /// </para>
    /// <para>Claiming a job releases its <see cref="JobInfo.DeduplicationKey"/>, so a later create with that key creates a new job.</para>
    /// </remarks>
    Task<JobInfo?> ClaimAsync(JobClaimRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims up to <paramref name="maxCount"/> jobs at once, each as <see cref="ClaimAsync"/> claims one and with its
    /// own <see cref="JobInfo.ClaimToken"/>, those due longest first, never two with the same name and
    /// <see cref="JobInfo.ConcurrencyKey"/>. Returns an empty list when none is due, and may
    /// return fewer than are due. Workers call this to fill their free slots, so a store with a network round trip
    /// should claim the batch in one; the default claims one job with <see cref="ClaimAsync"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxCount"/> is less than 1.</exception>
    async Task<IReadOnlyList<JobInfo>> ClaimBatchAsync(JobClaimRequest request, int maxCount, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);
        return await ClaimAsync(request, cancellationToken).ConfigureAwait(false) is { } job ? [job] : [];
    }

    /// <summary>
    /// Extends the lease of a running job if <paramref name="claimToken"/> still holds it, including after the lease
    /// expired as long as no other worker has claimed the job since.
    /// </summary>
    Task<JobLeaseStatus> RenewAsync(string jobId, string claimToken, TimeSpan leaseDuration, CancellationToken cancellationToken = default);

    /// <summary>Records progress if <paramref name="claimToken"/> still holds the job.</summary>
    Task<bool> ReportProgressAsync(string jobId, string claimToken, int? percent, string? message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finishes or requeues a running job if <paramref name="claimToken"/> still holds it, even when its lease has
    /// expired, as long as no other worker has claimed it since. Clears the claim. A job that succeeds drops its
    /// <see cref="JobInfo.Payload"/> and keeps the rest of its record until <see cref="JobCompletion.RetainFor"/> runs
    /// out (at once for a zero <see cref="JobCompletion.RetainFor"/>), so retained history costs little; failed
    /// and cancelled jobs keep their payload. Releases the job's <see cref="JobInfo.ConcurrencyKey"/>, so the next job
    /// waiting for it becomes claimable.
    /// </summary>
    Task<bool> CompleteAsync(string jobId, string claimToken, JobCompletion completion, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels a queued job immediately (retaining it for <paramref name="retainFor"/> and releasing its
    /// <see cref="JobInfo.DeduplicationKey"/>), or flags a running job so <see cref="RenewAsync"/> reports
    /// <see cref="JobLeaseStatus.CancellationRequested"/>. Returns false for finished or missing jobs.
    /// </summary>
    Task<bool> RequestCancellationAsync(string jobId, TimeSpan retainFor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a job in any status, releasing its <see cref="JobInfo.ConcurrencyKey"/> and <see cref="JobInfo.DeduplicationKey"/>.
    /// A worker still running it can no longer renew, report progress or complete it.
    /// </summary>
    Task<bool> DeleteAsync(string jobId, CancellationToken cancellationToken = default);
}
