using System;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Resilience;

/// <summary>
/// Defines a contract for executing actions with resilience policies, such as retries, circuit breakers, or timeouts.
/// </summary>
public interface IResiliencePolicy
{
    /// <summary>
    /// Executes the specified asynchronous action using the resilience policy.
    /// </summary>
    /// <param name="action">The asynchronous action to execute. The <see cref="CancellationToken"/> parameter allows the action to observe cancellation requests.</param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the operation.</param>
    /// <returns>A <see cref="ValueTask"/> representing the asynchronous operation.</returns>
    ValueTask ExecuteAsync(Func<CancellationToken, ValueTask> action, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes the specified asynchronous action using the resilience policy and returns a result.
    /// </summary>
    /// <typeparam name="T">The type of the result returned by the action.</typeparam>
    /// <param name="action">The asynchronous action to execute. The <see cref="CancellationToken"/> parameter allows the action to observe cancellation requests.</param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the operation.</param>
    /// <returns>A <see cref="ValueTask{T}"/> representing the asynchronous operation and its result.</returns>
    ValueTask<T> ExecuteAsync<T>(Func<CancellationToken, ValueTask<T>> action, CancellationToken cancellationToken = default);
}
