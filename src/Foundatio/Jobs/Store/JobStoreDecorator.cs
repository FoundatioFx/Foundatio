using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Jobs.Store;

/// <summary>
/// An <see cref="IJobStore"/> that passes every call to another one, including the members <see cref="IJobStore"/>
/// gives a default body. Derive from it to log, measure or fail some calls, and override only those: a wrapper that
/// implements <see cref="IJobStore"/> itself and forwards only the required members silently turns batch claims into
/// claims of one job and creates delayed jobs on the caller's clock, because the defaults call back into the wrapper.
/// </summary>
/// <example>
/// <code>
/// sealed class CountingJobStore(IJobStore inner) : JobStoreDecorator(inner)
/// {
///     public int Claims;
///
///     public override Task&lt;IReadOnlyList&lt;JobInfo&gt;&gt; ClaimBatchAsync(JobClaimRequest request, int maxCount, CancellationToken cancellationToken = default)
///     {
///         Interlocked.Increment(ref Claims);
///         return base.ClaimBatchAsync(request, maxCount, cancellationToken);
///     }
/// }
/// </code>
/// </example>
[Experimental(ProviderApi.DiagnosticId, UrlFormat = ProviderApi.UrlFormat)]
public abstract class JobStoreDecorator : IJobStore
{
    /// <param name="inner">The store every call goes to unless overridden.</param>
    protected JobStoreDecorator(IJobStore inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        Inner = inner;
    }

    /// <summary>The store every call goes to unless overridden.</summary>
    protected IJobStore Inner { get; }

    /// <inheritdoc />
    public virtual Task<string?> CreateAsync(JobInfo job, CancellationToken cancellationToken = default) => Inner.CreateAsync(job, cancellationToken);

    /// <inheritdoc />
    public virtual Task<string?> CreateAsync(JobInfo job, TimeSpan availableAfter, CancellationToken cancellationToken = default) => Inner.CreateAsync(job, availableAfter, cancellationToken);

    /// <inheritdoc />
    public virtual Task<JobInfo?> GetAsync(string jobId, CancellationToken cancellationToken = default) => Inner.GetAsync(jobId, cancellationToken);

    /// <inheritdoc />
    public virtual Task<JobPage<JobInfo>> QueryAsync(JobQuery query, CancellationToken cancellationToken = default) => Inner.QueryAsync(query, cancellationToken);

    /// <inheritdoc />
    public virtual Task<long> CountAsync(JobState? state = null, string? name = null, CancellationToken cancellationToken = default) => Inner.CountAsync(state, name, cancellationToken);

    /// <inheritdoc />
    public virtual Task<JobInfo?> ClaimAsync(JobClaimRequest request, CancellationToken cancellationToken = default) => Inner.ClaimAsync(request, cancellationToken);

    /// <inheritdoc />
    public virtual Task<IReadOnlyList<JobInfo>> ClaimBatchAsync(JobClaimRequest request, int maxCount, CancellationToken cancellationToken = default) => Inner.ClaimBatchAsync(request, maxCount, cancellationToken);

    /// <inheritdoc />
    public virtual Task<JobLeaseStatus> RenewAsync(string jobId, string claimToken, TimeSpan leaseDuration, CancellationToken cancellationToken = default) => Inner.RenewAsync(jobId, claimToken, leaseDuration, cancellationToken);

    /// <inheritdoc />
    public virtual Task<bool> ReportProgressAsync(string jobId, string claimToken, int? percent, string? message, CancellationToken cancellationToken = default) => Inner.ReportProgressAsync(jobId, claimToken, percent, message, cancellationToken);

    /// <inheritdoc />
    public virtual Task<bool> CompleteAsync(string jobId, string claimToken, JobCompletion completion, CancellationToken cancellationToken = default) => Inner.CompleteAsync(jobId, claimToken, completion, cancellationToken);

    /// <inheritdoc />
    public virtual Task<bool> RequestCancellationAsync(string jobId, TimeSpan retainFor, CancellationToken cancellationToken = default) => Inner.RequestCancellationAsync(jobId, retainFor, cancellationToken);

    /// <inheritdoc />
    public virtual Task<bool> DeleteAsync(string jobId, CancellationToken cancellationToken = default) => Inner.DeleteAsync(jobId, cancellationToken);
}
