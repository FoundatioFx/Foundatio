using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Jobs;

/// <summary>Enqueues durable jobs and tracks them.</summary>
public interface IJobClient
{
    /// <summary>
    /// Enqueues the job whose <see cref="IDurableJob{TArgs}"/> takes these arguments. The job is identified by the
    /// arguments' runtime type (not <typeparamref name="TArgs"/>), so the caller does not need to reference the job
    /// class and can pass arguments typed as <see cref="object"/> or a base type.
    /// </summary>
    Task<JobHandle> EnqueueAsync<TArgs>(TArgs arguments, EnqueueOptions? options = null, CancellationToken cancellationToken = default) where TArgs : class;

    /// <summary>
    /// Enqueues the <see cref="IDurableJob"/> <typeparamref name="TJob"/>, which takes no arguments. Enqueue a legacy
    /// <see cref="IJob"/> as <c>EnqueueAsync&lt;LegacyJob&lt;TJob&gt;&gt;()</c>.
    /// </summary>
    Task<JobHandle> EnqueueAsync<TJob>(EnqueueOptions? options = null, CancellationToken cancellationToken = default) where TJob : class, IDurableJob;

    /// <summary>Returns the job, or null when it does not exist or its retention has run out.</summary>
    Task<JobDetails?> GetAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>Lists jobs, newest first.</summary>
    Task<JobPage<JobDetails>> QueryAsync(JobQuery? query = null, CancellationToken cancellationToken = default);

    /// <summary>Counts retained jobs, optionally only those in <paramref name="state"/> or named <paramref name="name"/>.</summary>
    Task<long> CountAsync(JobState? state = null, string? name = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels a queued job immediately, or signals a running job's cancellation token. Returns false when the job
    /// does not exist or has already finished.
    /// </summary>
    Task<bool> CancelAsync(string jobId, CancellationToken cancellationToken = default);
}
