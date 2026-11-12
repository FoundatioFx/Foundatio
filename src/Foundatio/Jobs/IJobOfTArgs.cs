using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Jobs;

/// <summary>
/// A durable background job that takes arguments. Enqueue it with <see cref="IJobClient.EnqueueAsync{TArgs}(TArgs, EnqueueOptions, System.Threading.CancellationToken)"/>
/// using only the arguments, so producers need not reference the job class.
/// </summary>
/// <remarks>
/// Return normally to succeed. Throw to fail; the job is retried according to its <see cref="RetryPolicy"/>.
/// The cancellation token fires when someone cancels the job, when the host shuts down, or when this worker can no
/// longer prove it holds the job's lease (renewals failed until the lease ran out, or another worker took the job). A
/// job interrupted by shutdown is run again later without using up an attempt; a job that lost its lease is run again
/// by whichever worker claims it next. Jobs run at least once, so make them safe to repeat.
/// </remarks>
public interface IJob<in TArgs> where TArgs : class
{
    Task RunAsync(TArgs arguments, IJobContext context, CancellationToken cancellationToken);
}
