using System;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Resilience;

/// <summary>
/// Defines a contract for executing actions with resilience policies, such as retries, circuit breakers, or timeouts.
/// </summary>
public interface IResiliencePolicy
{
    // ============================================================
    // SYNCHRONOUS METHODS
    // ============================================================

    /// <summary>
    /// Executes the specified action using the resilience policy.
    /// </summary>
    /// <param name="action">The action to execute. The <see cref="CancellationToken"/> parameter allows the action to observe cancellation requests.</param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the operation.</param>
    void Execute(Action<CancellationToken> action, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes the specified action using the resilience policy and returns a result.
    /// </summary>
    /// <typeparam name="TResult">The type of the result returned by the action.</typeparam>
    /// <param name="action">The action to execute. The <see cref="CancellationToken"/> parameter allows the action to observe cancellation requests.</param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the operation.</param>
    /// <returns>The result of the action.</returns>
    TResult Execute<TResult>(Func<CancellationToken, TResult> action, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes the specified action using the resilience policy, passing state to avoid closure allocations.
    /// </summary>
    /// <typeparam name="TState">The type of the state to pass to the action.</typeparam>
    /// <param name="state">The state to pass to the action.</param>
    /// <param name="action">The action to execute. Receives the state and a <see cref="CancellationToken"/>.</param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the operation.</param>
    void Execute<TState>(TState state, Action<TState, CancellationToken> action, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes the specified action using the resilience policy, passing state to avoid closure allocations, and returns a result.
    /// </summary>
    /// <typeparam name="TState">The type of the state to pass to the action.</typeparam>
    /// <typeparam name="TResult">The type of the result returned by the action.</typeparam>
    /// <param name="state">The state to pass to the action.</param>
    /// <param name="action">The action to execute. Receives the state and a <see cref="CancellationToken"/>.</param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the operation.</param>
    /// <returns>The result of the action.</returns>
    TResult Execute<TState, TResult>(TState state, Func<TState, CancellationToken, TResult> action, CancellationToken cancellationToken = default);

    // ============================================================
    // ASYNCHRONOUS METHODS
    // ============================================================

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
    /// <typeparam name="TResult">The type of the result returned by the action.</typeparam>
    /// <param name="action">The asynchronous action to execute. The <see cref="CancellationToken"/> parameter allows the action to observe cancellation requests.</param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the operation.</param>
    /// <returns>A <see cref="ValueTask{TResult}"/> representing the asynchronous operation and its result.</returns>
    ValueTask<TResult> ExecuteAsync<TResult>(Func<CancellationToken, ValueTask<TResult>> action, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes the specified asynchronous action using the resilience policy, passing state to avoid closure allocations.
    /// </summary>
    /// <typeparam name="TState">The type of the state to pass to the action.</typeparam>
    /// <param name="state">The state to pass to the action.</param>
    /// <param name="action">The asynchronous action to execute. Receives the state and a <see cref="CancellationToken"/>.</param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the operation.</param>
    /// <returns>A <see cref="ValueTask"/> representing the asynchronous operation.</returns>
    ValueTask ExecuteAsync<TState>(TState state, Func<TState, CancellationToken, ValueTask> action, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes the specified asynchronous action using the resilience policy, passing state to avoid closure allocations, and returns a result.
    /// </summary>
    /// <typeparam name="TState">The type of the state to pass to the action.</typeparam>
    /// <typeparam name="TResult">The type of the result returned by the action.</typeparam>
    /// <param name="state">The state to pass to the action.</param>
    /// <param name="action">The asynchronous action to execute. Receives the state and a <see cref="CancellationToken"/>.</param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the operation.</param>
    /// <returns>A <see cref="ValueTask{TResult}"/> representing the asynchronous operation and its result.</returns>
    ValueTask<TResult> ExecuteAsync<TState, TResult>(TState state, Func<TState, CancellationToken, ValueTask<TResult>> action, CancellationToken cancellationToken = default);
}
