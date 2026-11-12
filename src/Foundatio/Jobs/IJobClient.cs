using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Jobs;

/// <summary>Enqueues durable jobs and tracks them.</summary>
public interface IJobClient
{
    /// <summary>
    /// Enqueues the job whose <see cref="IJob{TArgs}"/> takes these arguments. The job is identified by the
    /// arguments' runtime type (not <typeparamref name="TArgs"/>), so the caller does not need to reference the job
    /// class and can pass arguments typed as <see cref="object"/> or a base type.
    /// </summary>
    Task<JobHandle> EnqueueAsync<TArgs>(TArgs arguments, EnqueueOptions? options = null, CancellationToken cancellationToken = default) where TArgs : class;

    /// <summary>Enqueues a job that takes no arguments.</summary>
    Task<JobHandle> EnqueueAsync<TJob>(EnqueueOptions? options = null, CancellationToken cancellationToken = default) where TJob : IJob;

    Task<JobInfo?> GetAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>Lists jobs, newest first.</summary>
    Task<JobPage> QueryAsync(JobQuery? query = null, CancellationToken cancellationToken = default);

    Task<long> CountAsync(JobState? state = null, string? name = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels a queued job immediately, or signals a running job's cancellation token. Returns false when the job
    /// does not exist or has already finished.
    /// </summary>
    Task<bool> CancelAsync(string jobId, CancellationToken cancellationToken = default);
}
