using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Jobs.Store;

namespace Foundatio.Tests.Hosting;

/// <summary>An in-memory job store that can be made to fail, to drive worker and scheduler health through their states.</summary>
public sealed class FlakyJobStore(TimeProvider timeProvider) : IJobStore
{
    private readonly InMemoryJobStore _inner = new(timeProvider);

    /// <summary>Claiming throws.</summary>
    public bool FailClaim { get; set; }

    /// <summary>Creating jobs throws.</summary>
    public bool FailCreate { get; set; }

    /// <summary>Claiming returns a job without a name, which a correct store never does, so the claim loop crashes.</summary>
    public bool ReturnCorruptJob { get; set; }

    public Task<bool> CreateAsync(JobInfo job, CancellationToken cancellationToken = default)
        => FailCreate ? throw new InvalidOperationException("The job store is read-only") : _inner.CreateAsync(job, cancellationToken);

    public async Task<JobInfo?> ClaimAsync(JobClaimRequest request, CancellationToken cancellationToken = default)
    {
        if (ReturnCorruptJob)
            return new JobInfo { Id = "corrupt", Name = null! };
        if (FailClaim)
            throw new InvalidOperationException("The job store timed out");

        return await _inner.ClaimAsync(request, cancellationToken);
    }

    public Task<JobInfo?> GetAsync(string jobId, CancellationToken cancellationToken = default) => _inner.GetAsync(jobId, cancellationToken);
    public Task<JobPage<JobInfo>> QueryAsync(JobQuery query, CancellationToken cancellationToken = default) => _inner.QueryAsync(query, cancellationToken);
    public Task<long> CountAsync(JobState? state = null, string? name = null, CancellationToken cancellationToken = default) => _inner.CountAsync(state, name, cancellationToken);
    public Task<JobLeaseStatus> RenewAsync(string jobId, string claimToken, TimeSpan leaseDuration, CancellationToken cancellationToken = default) => _inner.RenewAsync(jobId, claimToken, leaseDuration, cancellationToken);
    public Task<bool> ReportProgressAsync(string jobId, string claimToken, int? percent, string? message, CancellationToken cancellationToken = default) => _inner.ReportProgressAsync(jobId, claimToken, percent, message, cancellationToken);
    public Task<bool> CompleteAsync(string jobId, string claimToken, JobCompletion completion, CancellationToken cancellationToken = default) => _inner.CompleteAsync(jobId, claimToken, completion, cancellationToken);
    public Task<bool> RequestCancellationAsync(string jobId, TimeSpan retainFor, CancellationToken cancellationToken = default) => _inner.RequestCancellationAsync(jobId, retainFor, cancellationToken);
    public Task<bool> DeleteAsync(string jobId, CancellationToken cancellationToken = default) => _inner.DeleteAsync(jobId, cancellationToken);
}
