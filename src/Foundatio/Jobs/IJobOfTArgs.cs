using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Jobs;

/// <summary>
/// A durable background job that takes arguments. Enqueue it with <see cref="IJobClient.EnqueueAsync{TArgs}(TArgs, EnqueueOptions, System.Threading.CancellationToken)"/>
/// using only the arguments, so producers need not reference the job class.
/// </summary>
/// <remarks>
/// Return normally to succeed. Throw to fail; the job is retried according to its <see cref="RetryPolicy"/>.
/// The cancellation token fires when someone cancels the job or the host shuts down; a job interrupted by shutdown
/// is run again later without using up an attempt. Jobs run at least once, so make them safe to repeat.
/// </remarks>
public interface IJob<in TArgs> where TArgs : class
{
    Task RunAsync(TArgs arguments, IJobContext context, CancellationToken cancellationToken);
}
