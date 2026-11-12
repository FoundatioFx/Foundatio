using System;
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
    /// values may be empty); every read of the job returns them. Returns false, changing nothing, when a job with the
    /// same id exists (including a finished job that is still retained). <see cref="JobInfo.CreatedAt"/> and
    /// <see cref="JobInfo.AvailableAt"/> come from the caller's clock.
    /// </summary>
    Task<bool> CreateAsync(JobInfo job, CancellationToken cancellationToken = default);

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
    Task<JobInfo?> ClaimAsync(JobClaimRequest request, CancellationToken cancellationToken = default);

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
    /// out, so retained history costs little; failed and cancelled jobs keep their payload.
    /// </summary>
    Task<bool> CompleteAsync(string jobId, string claimToken, JobCompletion completion, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels a queued job immediately (retaining it for <paramref name="retainFor"/>), or flags a running job so
    /// <see cref="RenewAsync"/> reports <see cref="JobLeaseStatus.CancellationRequested"/>. Returns false for finished or missing jobs.
    /// </summary>
    Task<bool> RequestCancellationAsync(string jobId, TimeSpan retainFor, CancellationToken cancellationToken = default);

    /// <summary>Removes a job in any status. A worker still running it can no longer renew, report progress or complete it.</summary>
    Task<bool> DeleteAsync(string jobId, CancellationToken cancellationToken = default);
}
