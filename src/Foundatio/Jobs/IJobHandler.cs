using System.Threading;
using System.Threading.Tasks;
using Foundatio.Resilience;

namespace Foundatio.Jobs;

/// <summary>
/// A durable background job that takes no arguments. Enqueue it by type with
/// <see cref="IJobClient.EnqueueAsync{TJob}(EnqueueOptions, CancellationToken)"/>, or run it on a CRON schedule with
/// <see cref="FoundatioJobsBuilder.AddCronJob{TJob}(string, CronJobOptions)"/>. It is named after its class, or its
/// <see cref="JobAttribute.Name"/>.
/// </summary>
/// <remarks>
/// Return normally to succeed. Throw to fail; the job is retried according to its <see cref="RetryPolicy"/>.
/// <see cref="IJobContext.ScheduledFor"/> is the occurrence a CRON run is for, so key idempotency on it rather than on
/// the current time. The cancellation token fires when someone cancels the job, when the host shuts down, or when this
/// worker can no longer prove it holds the job's lease. Jobs run at least once, so make them safe to repeat.
/// </remarks>
public interface IJobHandler
{
    /// <summary>Handles the job.</summary>
    /// <param name="context">The job's id, attempt and scheduled time, and a way to report progress.</param>
    /// <param name="cancellationToken">Fires when the job is cancelled, the host stops or the lease is lost.</param>
    Task HandleAsync(IJobContext context, CancellationToken cancellationToken);
}
