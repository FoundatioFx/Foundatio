using System;

namespace Foundatio.Resilience;

/// <summary>
/// Implements the circuit breaker pattern to prevent cascading failures.
/// When failures exceed a threshold, the circuit "opens" to fail fast and allow the system to recover.
/// </summary>
/// <remarks>
/// Circuit states:
/// <list type="bullet">
///   <item><description><b>Closed</b>: Normal operation, calls are allowed.</description></item>
///   <item><description><b>Open</b>: Failures exceeded threshold, calls fail immediately.</description></item>
///   <item><description><b>HalfOpen</b>: Testing recovery, limited calls allowed.</description></item>
/// </list>
/// </remarks>
public interface ICircuitBreaker
{
    /// <summary>
    /// Gets the current state of the circuit breaker.
    /// </summary>
    CircuitState State { get; }

    /// <summary>
    /// Called before executing a protected operation.
    /// Throws if the circuit is open and calls should not be attempted.
    /// </summary>
    void BeforeCall();

    /// <summary>
    /// Records a successful call, potentially closing the circuit if in half-open state.
    /// </summary>
    void RecordCallSuccess();

    /// <summary>
    /// Records a failed call, potentially opening the circuit if failures exceed the threshold.
    /// </summary>
    /// <param name="ex">The exception that caused the failure.</param>
    void RecordCallFailure(Exception ex);
}
