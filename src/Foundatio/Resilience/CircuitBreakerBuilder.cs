using System;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;

namespace Foundatio.Resilience;

public class CircuitBreakerBuilder
{
    private readonly CircuitBreaker _circuitBreaker;

    public CircuitBreakerBuilder(ILogger logger = null, TimeProvider timeProvider = null)
    {
        _circuitBreaker = new CircuitBreaker(logger, timeProvider);
    }

    public CircuitBreakerBuilder(CircuitBreaker circuitBreaker)
    {
        _circuitBreaker = circuitBreaker ?? throw new ArgumentNullException(nameof(circuitBreaker));
    }

    /// <summary>
    /// Sets the logger for the policy.
    /// </summary>
    /// <param name="logger"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public CircuitBreakerBuilder WithLogger(ILogger logger)
    {
        _circuitBreaker.Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        return this;
    }

    /// <summary>
    /// Sets the duration for which the circuit breaker samples calls to determine if it should open.
    /// </summary>
    /// <param name="samplingDuration"></param>
    /// <returns></returns>
    public CircuitBreakerBuilder WithSamplingDuration(TimeSpan samplingDuration)
    {
        _circuitBreaker.SamplingDuration = samplingDuration;
        return this;
    }

    /// <summary>
    /// Sets the failure ratio that determines when the circuit breaker should open.
    /// </summary>
    /// <param name="failureRatio"></param>
    /// <returns></returns>
    public CircuitBreakerBuilder WithFailureRatio(double failureRatio)
    {
        _circuitBreaker.FailureRatio = failureRatio;
        return this;
    }

    /// <summary>
    /// Sets the minimum number of calls that must be made during the sampling period before the circuit breaker can open.
    /// </summary>
    /// <param name="minimumCalls"></param>
    /// <returns></returns>
    public CircuitBreakerBuilder WithMinimumCalls(int minimumCalls)
    {
        _circuitBreaker.MinimumCalls = minimumCalls;
        return this;
    }

    /// <summary>
    /// Sets the duration for which the circuit breaker will remain open before it allows calls to be made again.
    /// </summary>
    /// <param name="breakDuration"></param>
    /// <returns></returns>
    public CircuitBreakerBuilder WithBreakDuration(TimeSpan breakDuration)
    {
        _circuitBreaker.BreakDuration = breakDuration;
        return this;
    }

    /// <summary>
    /// Adds an exception type that will not be recorded by the circuit breaker. These exceptions will not trigger the circuit breaker to open.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public CircuitBreakerBuilder WithUnrecordedException<T>() where T : Exception
    {
        _circuitBreaker.UnrecordedExceptions.Add(typeof(T));
        return this;
    }

    /// <summary>
    /// Adds exception types that will not be recorded by the circuit breaker. These exceptions will not trigger the circuit breaker to open.
    /// </summary>
    /// <param name="unrecordedExceptionTypes"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public CircuitBreakerBuilder WithUnrecordedException(params Type[] unrecordedExceptionTypes)
    {
        if (unrecordedExceptionTypes == null)
            throw new ArgumentNullException(nameof(unrecordedExceptionTypes));

        _circuitBreaker.UnrecordedExceptions.AddRange(unrecordedExceptionTypes);
        return this;
    }

    /// <summary>
    /// Sets a function that determines whether to record an exception.
    /// </summary>
    /// <param name="shouldRecord"></param>
    /// <returns></returns>
    public CircuitBreakerBuilder WithShouldRecord(Func<Exception, bool> shouldRecord)
    {
        _circuitBreaker.ShouldRecord = shouldRecord;
        return this;
    }

    /// <summary>
    /// Builds the circuit breaker with the configured settings.
    /// </summary>
    /// <returns></returns>
    public CircuitBreaker Build() => _circuitBreaker;
}
