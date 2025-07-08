using System;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;

namespace Foundatio.Resilience;

public class ResiliencePolicyBuilder
{
    private readonly ResiliencePolicy _policy;

    public ResiliencePolicyBuilder(ILogger logger = null, TimeProvider timeProvider = null)
    {
        _policy = new ResiliencePolicy(logger, timeProvider);
    }

    public ResiliencePolicyBuilder(ResiliencePolicy policy)
    {
        _policy = policy;
    }

    /// <summary>
    /// Sets the logger for the policy.
    /// </summary>
    /// <param name="logger"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public ResiliencePolicyBuilder WithLogger(ILogger logger)
    {
        _policy.Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        return this;
    }

    /// <summary>
    /// Sets the maximum number of attempts for the policy.
    /// </summary>
    /// <param name="maxAttempts"></param>
    public ResiliencePolicyBuilder WithMaxAttempts(int maxAttempts)
    {
        _policy.MaxAttempts = maxAttempts;
        return this;
    }

    /// <summary>
    /// Adds an exception type that will not be handled by the policy. These exceptions will be thrown immediately without retrying.
    /// </summary>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public ResiliencePolicyBuilder WithUnhandledException<T>() where T : Exception
    {
        _policy.UnhandledExceptions.Add(typeof(T));
        return this;
    }

    /// <summary>
    /// Adds exception types that will not be handled by the policy. These exceptions will be thrown immediately without retrying.
    /// </summary>
    /// <param name="unhandledExceptionTypes"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public ResiliencePolicyBuilder WithUnhandledException(params Type[] unhandledExceptionTypes)
    {
        if (unhandledExceptionTypes == null)
            throw new ArgumentNullException(nameof(unhandledExceptionTypes));

        _policy.UnhandledExceptions.AddRange(unhandledExceptionTypes);
        return this;
    }

    /// <summary>
    /// Sets a function that determines whether to retry based on the attempt number and exception.
    /// </summary>
    /// <param name="shouldRetry"></param>
    public ResiliencePolicyBuilder WithShouldRetry(Func<int, Exception, bool> shouldRetry)
    {
        _policy.ShouldRetry = shouldRetry;
        return this;
    }

    /// <summary>
    /// Sets a fixed retry delay for all retries.
    /// </summary>
    /// <param name="retryDelay"></param>
    public ResiliencePolicyBuilder WithDelay(TimeSpan? retryDelay)
    {
        _policy.Delay = retryDelay;
        return this;
    }

    /// <summary>
    /// Sets a function that returns the retry delay based on the number of attempts. This overrides the fixed delay.
    /// </summary>
    /// <param name="getDelay"></param>
    public ResiliencePolicyBuilder WithGetDelay(Func<int, TimeSpan> getDelay)
    {
        _policy.GetDelay = getDelay;
        return this;
    }

    /// <summary>
    /// Sets an exponential delay for retries based on a base delay and an optional exponential factor.
    /// </summary>
    /// <param name="baseDelay"></param>
    /// <param name="exponentialFactor"></param>
    public ResiliencePolicyBuilder WithExponentialDelay(TimeSpan? baseDelay = null, double exponentialFactor = 2.0)
    {
        _policy.GetDelay = ResiliencePolicy.ExponentialDelay(baseDelay ?? TimeSpan.FromSeconds(1), exponentialFactor);
        return this;
    }

    /// <summary>
    /// Sets a linear delay for retries based on a base delay.
    /// </summary>
    /// <param name="baseDelay"></param>
    public ResiliencePolicyBuilder WithLinearDelay(TimeSpan? baseDelay = null)
    {
        _policy.GetDelay = ResiliencePolicy.LinearDelay(baseDelay ?? TimeSpan.FromSeconds(1));
        return this;
    }

    /// <summary>
    /// Sets the maximum retry delay for all retries.
    /// </summary>
    /// <param name="maxDelay"></param>
    public ResiliencePolicyBuilder WithMaxDelay(TimeSpan? maxDelay)
    {
        _policy.MaxDelay = maxDelay;
        return this;
    }

    /// <summary>
    /// Sets whether to use jitter in the backoff interval.
    /// </summary>
    /// <param name="useJitter"></param>
    public ResiliencePolicyBuilder WithJitter(bool useJitter = true)
    {
        _policy.UseJitter = useJitter;
        return this;
    }

    /// <summary>
    /// Sets the timeout for the entire operation. If set to zero, no timeout is applied.
    /// </summary>
    /// <param name="timeout"></param>
    public ResiliencePolicyBuilder WithTimeout(TimeSpan timeout)
    {
        _policy.Timeout = timeout;
        return this;
    }

    /// <summary>
    /// Sets the circuit breaker for this policy with a default configuration.
    /// </summary>
    public ResiliencePolicyBuilder WithCircuitBreaker()
    {
        _policy.CircuitBreaker = new CircuitBreaker(_policy.Logger, _policy.GetTimeProvider());
        return this;
    }

    /// <summary>
    /// Sets the circuit breaker for this policy. If set to null, no circuit breaker is applied.
    /// </summary>
    /// <param name="circuitBreaker"></param>
    public ResiliencePolicyBuilder WithCircuitBreaker(ICircuitBreaker circuitBreaker)
    {
        _policy.CircuitBreaker = circuitBreaker;
        return this;
    }

    /// <summary>
    /// Sets the circuit breaker for this policy using a builder.
    /// </summary>
    /// <param name="circuitBreaker"></param>
    public ResiliencePolicyBuilder WithCircuitBreaker(Action<CircuitBreakerBuilder> circuitBreaker)
    {
        if (circuitBreaker == null)
            throw new ArgumentNullException(nameof(circuitBreaker));

        var cb = new CircuitBreaker(_policy.Logger, _policy.GetTimeProvider());
        var builder = new CircuitBreakerBuilder(cb);
        circuitBreaker(builder);

        return WithCircuitBreaker(cb);
    }

    /// <summary>
    /// Builds the resilience policy with the configured settings.
    /// </summary>
    /// <returns></returns>
    public ResiliencePolicy Build() => _policy;
}
