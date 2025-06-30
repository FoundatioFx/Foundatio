using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Utility.Resilience;

public interface IResiliencePolicyProvider
{
    IResiliencePolicy GetPolicy(string name = null);
}

public interface IResiliencePolicy
{
    ValueTask ExecuteAsync(Func<CancellationToken, ValueTask> action, CancellationToken cancellationToken = default);
    ValueTask<T> ExecuteAsync<T>(Func<CancellationToken, ValueTask<T>> action, CancellationToken cancellationToken = default);
}

public interface IHaveResiliencePolicyProvider
{
    IResiliencePolicyProvider ResiliencePolicyProvider { get; }
}

public class ResiliencePolicyProvider : IResiliencePolicyProvider
{
    private readonly ConcurrentDictionary<string, IResiliencePolicy> _policies = new(StringComparer.OrdinalIgnoreCase);
    private IResiliencePolicy _defaultPolicy;
    private readonly TimeProvider _timeProvider;
    private readonly ILoggerFactory _loggerFactory;

    public ResiliencePolicyProvider(TimeProvider timeProvider = null, ILoggerFactory loggerFactory = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _defaultPolicy = new ResiliencePolicy(_timeProvider, _loggerFactory.CreateLogger<ResiliencePolicy>())
        {
            MaxAttempts = 5
        };
    }

    public ResiliencePolicyProvider WithDefaultPolicy(IResiliencePolicy policy)
    {
        _defaultPolicy = policy ?? throw new ArgumentNullException(nameof(policy));
        return this;
    }

    public ResiliencePolicyProvider WithDefaultPolicy(Action<ResiliencePolicyBuilder> builder)
    {
        if (builder == null)
            throw new ArgumentNullException(nameof(builder));

        var policy = new ResiliencePolicy(_timeProvider, _loggerFactory.CreateLogger<ResiliencePolicy>());
        var policyBuilder = new ResiliencePolicyBuilder(policy);
        builder(policyBuilder);

        _defaultPolicy = policy;
        return this;
    }

    public ResiliencePolicyProvider WithPolicy(string name, IResiliencePolicy policy)
    {
        if (name == null)
            throw new ArgumentNullException(nameof(name));

        _policies[name] = policy ?? throw new ArgumentNullException(nameof(policy));
        return this;
    }

    public ResiliencePolicyProvider WithPolicy(string name, Action<ResiliencePolicyBuilder> builder)
    {
        if (name == null)
            throw new ArgumentNullException(nameof(name));

        if (builder == null)
            throw new ArgumentNullException(nameof(builder));

        var policy = new ResiliencePolicy(_timeProvider, _loggerFactory.CreateLogger<ResiliencePolicy>());
        var policyBuilder = new ResiliencePolicyBuilder(policy);
        builder(policyBuilder);

        _policies[name] = policy;
        return this;
    }

    public IResiliencePolicy GetPolicy(string name = null)
    {
        if (name == null)
            return _defaultPolicy;

        return _policies.TryGetValue(name, out var policy) ? policy : _defaultPolicy;
    }
}

public class ResiliencePolicy : IResiliencePolicy, IHaveTimeProvider, IHaveLogger
{
    private readonly TimeProvider _timeProvider;
    private ILogger _logger;

    public ResiliencePolicy(TimeProvider timeProvider = null, ILogger logger = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
    }

    ILogger IHaveLogger.Logger => _logger;
    TimeProvider IHaveTimeProvider.TimeProvider => _timeProvider;

    /// <summary>
    /// Gets or sets the logger for this policy.
    /// </summary>
    public ILogger Logger {
        get => _logger;
        set => _logger = value ?? NullLogger.Instance;
    }

    /// <summary>
    /// The maximum number of attempts to execute the action. Default is 3 attempts.
    /// </summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>
    /// A collection of exception types that will not be handled by the policy. These exceptions will be thrown immediately without retrying. Default includes OperationCanceledException.
    /// </summary>
    public HashSet<Type> UnhandledExceptions { get; set; } = [ typeof(OperationCanceledException) ];

    /// <summary>
    /// A function that determines whether to retry based on the attempt number and exception.
    /// </summary>
    public Func<int, Exception, bool> ShouldRetry { get; set; }

    /// <summary>
    /// Sets a fixed retry delay for all retries.
    /// </summary>
    public TimeSpan? Delay { get; set; }

    /// <summary>
    /// Gets or sets a function that returns the retry delay based on the number of attempts. Default is an exponential delay starting at 1 second.
    /// </summary>
    public Func<int, TimeSpan> GetDelay { get; set; } = ExponentialDelay(TimeSpan.FromSeconds(1));

    /// <summary>
    /// Sets the max retry delay for all retries. Default is null, meaning no maximum delay.
    /// </summary>
    public TimeSpan? MaxDelay { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to use jitter in the backoff interval. Default is false.
    /// </summary>
    public bool UseJitter { get; set; }

    /// <summary>
    /// Gets or sets the timeout for the entire operation.
    /// </summary>
    public TimeSpan Timeout { get; set; }

    /// <summary>
    /// Gets or sets the circuit breaker for this policy.
    /// </summary>
    public ICircuitBreaker CircuitBreaker { get; set; }

    public async ValueTask ExecuteAsync(Func<CancellationToken, ValueTask> action, CancellationToken cancellationToken = default)
    {
        _ = await ExecuteAsync(async ct =>
        {
            await action(ct);
            return (object)null;
        }, cancellationToken);
    }

    public async ValueTask<T> ExecuteAsync<T>(Func<CancellationToken, ValueTask<T>> action, CancellationToken cancellationToken = default)
    {
        if (action == null)
            throw new ArgumentNullException(nameof(action));

        cancellationToken.ThrowIfCancellationRequested();

        int attempts = 1;
        var startTime = _timeProvider.GetUtcNow();
        var linkedCancellationToken = cancellationToken;
        if (Timeout > TimeSpan.Zero)
            linkedCancellationToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, new CancellationTokenSource(Timeout).Token).Token;

        do
        {
            if (attempts > 1)
                _logger?.LogInformation("Retrying {Attempts} attempt after {Duration:g}...", attempts.ToOrdinal(), _timeProvider.GetUtcNow().Subtract(startTime));

            try
            {
                CircuitBreaker?.BeforeCall();
                var result = await action(linkedCancellationToken).AnyContext();
                CircuitBreaker?.RecordCallSuccess();
                return result;
            }
            catch (BrokenCircuitException)
            {
                throw;
            }
            catch (Exception ex)
            {
                CircuitBreaker?.RecordCallFailure(ex);

                if (attempts >= MaxAttempts || (ShouldRetry != null && !ShouldRetry(attempts, ex)) || UnhandledExceptions.Contains(ex.GetType()))
                    throw;

                _logger?.LogError(ex, "Retry error: {Message}", ex.Message);

                await _timeProvider.SafeDelay(GetAttemptDelay(attempts), linkedCancellationToken).AnyContext();

                ThrowIfTimedOut(startTime);
            }

            attempts++;
        } while (attempts <= MaxAttempts && !linkedCancellationToken.IsCancellationRequested);

        throw new TaskCanceledException("Should not get here");
    }

    private void ThrowIfTimedOut(DateTimeOffset startTime)
    {
        if (Timeout <= TimeSpan.Zero)
            return;

        var elapsed = _timeProvider.GetUtcNow().Subtract(startTime);
        if (elapsed >= Timeout)
            throw new TimeoutException($"Operation timed out after {Timeout:g}.");
    }

    private TimeSpan GetAttemptDelay(int attempts)
    {
        var delay = Delay ?? GetDelay?.Invoke(attempts) ?? TimeSpan.FromMilliseconds(100);

        if (UseJitter)
        {
            double offset = delay.TotalMilliseconds * 0.5 / 2;
            double randomDelay = delay.TotalMilliseconds * 0.5 * _random.NextDouble() - offset;
            double newDelay = delay.TotalMilliseconds + randomDelay;
            delay = TimeSpan.FromMilliseconds(newDelay);
        }

        if (delay < TimeSpan.Zero)
            delay = TimeSpan.Zero;

        if (MaxDelay.HasValue && delay > MaxDelay.Value)
            delay = MaxDelay.Value;

        return delay;
    }

    private static readonly Random _random = new();

    public static Func<int, TimeSpan> ExponentialDelay(TimeSpan baseDelay, double exponentialFactor = 2.0)
    {
        return attempt => TimeSpan.FromMilliseconds(Math.Pow(exponentialFactor, attempt) * baseDelay.TotalMilliseconds);
    }

    public static Func<int, TimeSpan> LinearDelay(TimeSpan baseDelay)
    {
        return attempt => TimeSpan.FromMilliseconds((attempt + 1) * baseDelay.TotalMilliseconds);
    }
}

public interface ICircuitBreaker
{
    CircuitState State { get; }
    void BeforeCall();
    void RecordCallSuccess();
    void RecordCallFailure(Exception ex);
}

public class CircuitBreaker : ICircuitBreaker, IHaveTimeProvider, IHaveLogger
{
    private readonly TimeProvider _timeProvider;
    private ILogger _logger;

    private CircuitState _state = CircuitState.Closed;
    private DateTime? _periodStartTime;
    private int _periodCalls;
    private int _periodFailures;
    private DateTime? _breakStartTime;
    private TimeSpan _currentBreakDuration;

    public CircuitBreaker(TimeProvider timeProvider = null, ILogger logger = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
    }

    ILogger IHaveLogger.Logger => _logger;
    TimeProvider IHaveTimeProvider.TimeProvider => _timeProvider;

    /// <summary>
    /// Gets or sets the logger for this circuit breaker.
    /// </summary>
    public ILogger Logger {
        get => _logger;
        set => _logger = value ?? NullLogger.Instance;
    }

    /// <summary>
    /// Gets or sets the duration for which the circuit breaker samples calls to determine if it should open. Default is 30 seconds.
    /// </summary>
    public TimeSpan SamplingDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the failure ratio that determines when the circuit breaker should open. Default is 0.1 (10%).
    /// </summary>
    public double FailureRatio { get; set; } = 0.1;

    /// <summary>
    /// Gets or sets the minimum number of calls that must be made during the sampling period before the circuit breaker can open. Default is 100.
    /// </summary>
    public int MinimumCalls { get; set; } = 100;

    /// <summary>
    /// Gets or sets the duration for which the circuit breaker will remain open before it allows calls to be made again. Default is 5 seconds.
    /// </summary>
    public TimeSpan BreakDuration { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A collection of exception types that will not be recorded by the circuit breaker. These exceptions will not trigger the circuit breaker to open. Default includes OperationCanceledException.
    /// </summary>
    public HashSet<Type> UnrecordedExceptions { get; set; } = [ typeof(OperationCanceledException) ];

    /// <summary>
    /// Gets or sets a function that determines whether to record an exception.
    /// </summary>
    public Func<Exception, bool> ShouldRecord { get; set; }

    /// <summary>
    /// Gets the current state of the circuit breaker.
    /// </summary>
    public CircuitState State => _state;

    /// <summary>
    /// Opens the circuit breaker manually, preventing any calls from being made. Close the circuit breaker to allow calls again.
    /// </summary>
    public void Open()
    {
        ChangeState(CircuitState.ManuallyOpen);
        _logger.LogInformation("Circuit breaker manually opened.");
    }

    /// <summary>
    /// Closes the circuit breaker, allowing calls to be made again. If the circuit was open, it will reset the break period.
    /// </summary>
    public void Close()
    {
        if (ChangeState(CircuitState.Closed))
            ResetPeriod();
    }

    void ICircuitBreaker.BeforeCall()
    {
        bool shouldTest = false;
        if (_state == CircuitState.Open && IsBreakOver() && TryChangeState(CircuitState.Open, CircuitState.HalfOpen))
        {
            StartBreak();
            shouldTest = true;
            _logger.LogInformation("Allowing test circuit breaker call.");
        }

        switch (State)
        {
            case CircuitState.Closed:
                break;
            case CircuitState.HalfOpen:
                if (!shouldTest)
                    throw new BrokenCircuitException();
                break;
            case CircuitState.Open:
            case CircuitState.ManuallyOpen:
                throw new BrokenCircuitException();
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    void ICircuitBreaker.RecordCallSuccess()
    {
        CheckPeriodStart();
        Interlocked.Increment(ref _periodCalls);

        if (_state != CircuitState.HalfOpen && _state != CircuitState.Open)
            return;

        if (ChangeState(CircuitState.Closed))
            ResetPeriod();
    }

    void ICircuitBreaker.RecordCallFailure(Exception ex)
    {
        CheckPeriodStart();
        int count = Interlocked.Increment(ref _periodCalls);

        if ((ShouldRecord != null && ShouldRecord.Invoke(ex) == false) || UnrecordedExceptions.Contains(ex.GetType()))
            return;

        if (_state == CircuitState.HalfOpen && TryChangeState(CircuitState.HalfOpen, CircuitState.Open))
            StartBreak();

        int failureCount = Interlocked.Increment(ref _periodFailures);

        if (count < MinimumCalls)
            return;

        double currentFailureRatio = (double)failureCount / count;
        if (currentFailureRatio < FailureRatio)
            return;

        if (TryChangeState(CircuitState.Closed, CircuitState.Open))
        {
            StartBreak();
            _logger.LogWarning("Circuit breaker opened due to failure ratio: {FailureRatio:F2} with {Failures} failures out of {Calls} calls.", currentFailureRatio, failureCount, count);
        }
    }

    private bool TryChangeState(CircuitState oldState, CircuitState newState)
    {
        var replacedState = (CircuitState)Interlocked.CompareExchange(
            ref Unsafe.As<CircuitState, int>(ref _state),
            (int)newState,
            (int)oldState
        );

        bool success = replacedState == oldState;
        if (!success)
            return false;

        _logger.LogInformation("Circuit state changed from {OldState} to {NewState}", replacedState, newState);

        return true;
    }

    private bool ChangeState(CircuitState newState)
    {
        var replacedState = (CircuitState)Interlocked.Exchange(
            ref Unsafe.As<CircuitState, int>(ref _state),
            (int)newState
        );

        bool changed = replacedState != newState;
        if (!changed)
            return false;

        _logger.LogInformation("Circuit state changed from {OldState} to {NewState}", replacedState, newState);

        return true;
    }

    private void StartBreak()
    {
        _breakStartTime = _timeProvider.GetUtcNow().UtcDateTime;
        _currentBreakDuration = BreakDuration;
    }

    private bool IsBreakOver()
    {
        if (!_breakStartTime.HasValue)
            return false;

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        return now.Subtract(_breakStartTime.Value) >= _currentBreakDuration;
    }

    private void CheckPeriodStart()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        _periodStartTime ??= now;

        if (now.Subtract(_periodStartTime.Value) < SamplingDuration)
            return;

        ResetPeriod();
    }

    private void ResetPeriod()
    {
        _periodStartTime = null;
        Interlocked.Exchange(ref _periodCalls, 0);
        Interlocked.Exchange(ref _periodFailures, 0);
    }
}

public enum CircuitState
{
    /// <summary>
    /// Attempts are allowed
    /// </summary>
    Closed,
    /// <summary>
    /// No attempts are allowed
    /// </summary>
    Open,
    /// <summary>
    /// Some attempts allowed to test
    /// </summary>
    HalfOpen,
    /// <summary>
    /// Circuit is manually opened and no attempts are allowed
    /// </summary>
    ManuallyOpen
}

public class BrokenCircuitException(string message = "The circuit is now open and is not allowing calls.") : Exception(message);

public class ResiliencePolicyBuilder(ResiliencePolicy policy)
{
    /// <summary>
    /// Sets the logger for the policy.
    /// </summary>
    /// <param name="logger"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public ResiliencePolicyBuilder WithLogger(ILogger logger)
    {
        policy.Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        return this;
    }

    /// <summary>
    /// Sets the maximum number of attempts for the policy.
    /// </summary>
    /// <param name="maxAttempts"></param>
    public ResiliencePolicyBuilder WithMaxAttempts(int maxAttempts)
    {
        policy.MaxAttempts = maxAttempts;
        return this;
    }

    /// <summary>
    /// Adds an exception type that will not be handled by the policy. These exceptions will be thrown immediately without retrying.
    /// </summary>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public ResiliencePolicyBuilder WithUnhandledException<T>() where T : Exception
    {
        policy.UnhandledExceptions.Add(typeof(T));
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

        policy.UnhandledExceptions.AddRange(unhandledExceptionTypes);
        return this;
    }

    /// <summary>
    /// Sets a function that determines whether to retry based on the attempt number and exception.
    /// </summary>
    /// <param name="shouldRetry"></param>
    public ResiliencePolicyBuilder WithShouldRetry(Func<int, Exception, bool> shouldRetry)
    {
        policy.ShouldRetry = shouldRetry;
        return this;
    }

    /// <summary>
    /// Sets a fixed retry delay for all retries.
    /// </summary>
    /// <param name="retryDelay"></param>
    public ResiliencePolicyBuilder WithDelay(TimeSpan? retryDelay)
    {
        policy.Delay = retryDelay;
        return this;
    }

    /// <summary>
    /// Sets a function that returns the retry delay based on the number of attempts. This overrides the fixed delay.
    /// </summary>
    /// <param name="getDelay"></param>
    public ResiliencePolicyBuilder WithGetDelay(Func<int, TimeSpan> getDelay)
    {
        policy.GetDelay = getDelay;
        return this;
    }

    /// <summary>
    /// Sets an exponential delay for retries based on a base delay and an optional exponential factor.
    /// </summary>
    /// <param name="baseDelay"></param>
    /// <param name="exponentialFactor"></param>
    public ResiliencePolicyBuilder WithExponentialDelay(TimeSpan? baseDelay = null, double exponentialFactor = 2.0)
    {
        policy.GetDelay = ResiliencePolicy.ExponentialDelay(baseDelay ?? TimeSpan.FromSeconds(1), exponentialFactor);
        return this;
    }

    /// <summary>
    /// Sets a linear delay for retries based on a base delay.
    /// </summary>
    /// <param name="baseDelay"></param>
    public ResiliencePolicyBuilder WithLinearDelay(TimeSpan? baseDelay = null)
    {
        policy.GetDelay = ResiliencePolicy.LinearDelay(baseDelay ?? TimeSpan.FromSeconds(1));
        return this;
    }

    /// <summary>
    /// Sets the maximum retry delay for all retries.
    /// </summary>
    /// <param name="maxDelay"></param>
    public ResiliencePolicyBuilder WithMaxDelay(TimeSpan? maxDelay)
    {
        policy.MaxDelay = maxDelay;
        return this;
    }

    /// <summary>
    /// Sets whether to use jitter in the backoff interval.
    /// </summary>
    /// <param name="useJitter"></param>
    public ResiliencePolicyBuilder WithJitter(bool useJitter = true)
    {
        policy.UseJitter = useJitter;
        return this;
    }

    /// <summary>
    /// Sets the timeout for the entire operation. If set to zero, no timeout is applied.
    /// </summary>
    /// <param name="timeout"></param>
    public ResiliencePolicyBuilder WithTimeout(TimeSpan timeout)
    {
        policy.Timeout = timeout;
        return this;
    }

    /// <summary>
    /// Sets the circuit breaker for this policy with a default configuration.
    /// </summary>
    public ResiliencePolicyBuilder WithCircuitBreaker()
    {
        policy.CircuitBreaker = new CircuitBreaker(policy.GetTimeProvider())
        {
            Logger = policy.Logger
        };
        return this;;
    }

    /// <summary>
    /// Sets the circuit breaker for this policy. If set to null, no circuit breaker is applied.
    /// </summary>
    /// <param name="circuitBreaker"></param>
    public ResiliencePolicyBuilder WithCircuitBreaker(ICircuitBreaker circuitBreaker)
    {
        policy.CircuitBreaker = circuitBreaker;
        return this;;
    }

    /// <summary>
    /// Sets the circuit breaker for this policy using a builder.
    /// </summary>
    /// <param name="circuitBreaker"></param>
    public ResiliencePolicyBuilder WithCircuitBreaker(Action<CircuitBreakerBuilder> circuitBreaker)
    {
        if (circuitBreaker == null)
            throw new ArgumentNullException(nameof(circuitBreaker));

        var cb = new CircuitBreaker(policy.GetTimeProvider(), policy.Logger);
        var builder = new CircuitBreakerBuilder(cb);
        circuitBreaker(builder);

        return WithCircuitBreaker(cb);
    }
}

public class CircuitBreakerBuilder(CircuitBreaker circuitBreaker)
{
    /// <summary>
    /// Sets the logger for the policy.
    /// </summary>
    /// <param name="logger"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public CircuitBreakerBuilder WithLogger(ILogger logger)
    {
        circuitBreaker.Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        return this;
    }

    /// <summary>
    /// Sets the duration for which the circuit breaker samples calls to determine if it should open.
    /// </summary>
    /// <param name="samplingDuration"></param>
    /// <returns></returns>
    public CircuitBreakerBuilder WithSamplingDuration(TimeSpan samplingDuration)
    {
        circuitBreaker.SamplingDuration = samplingDuration;
        return this;
    }

    /// <summary>
    /// Sets the failure ratio that determines when the circuit breaker should open.
    /// </summary>
    /// <param name="failureRatio"></param>
    /// <returns></returns>
    public CircuitBreakerBuilder WithFailureRatio(double failureRatio)
    {
        circuitBreaker.FailureRatio = failureRatio;
        return this;
    }

    /// <summary>
    /// Sets the minimum number of calls that must be made during the sampling period before the circuit breaker can open.
    /// </summary>
    /// <param name="minimumCalls"></param>
    /// <returns></returns>
    public CircuitBreakerBuilder WithMinimumCalls(int minimumCalls)
    {
        circuitBreaker.MinimumCalls = minimumCalls;
        return this;
    }

    /// <summary>
    /// Sets the duration for which the circuit breaker will remain open before it allows calls to be made again.
    /// </summary>
    /// <param name="breakDuration"></param>
    /// <returns></returns>
    public CircuitBreakerBuilder WithBreakDuration(TimeSpan breakDuration)
    {
        circuitBreaker.BreakDuration = breakDuration;
        return this;
    }

    /// <summary>
    /// Adds an exception type that will not be recorded by the circuit breaker. These exceptions will not trigger the circuit breaker to open.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public CircuitBreakerBuilder WithUnrecordedException<T>() where T : Exception
    {
        circuitBreaker.UnrecordedExceptions.Add(typeof(T));
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

        circuitBreaker.UnrecordedExceptions.AddRange(unrecordedExceptionTypes);
        return this;
    }

    /// <summary>
    /// Sets a function that determines whether to record an exception.
    /// </summary>
    /// <param name="shouldRecord"></param>
    /// <returns></returns>
    public CircuitBreakerBuilder WithShouldRecord(Func<Exception, bool> shouldRecord)
    {
        circuitBreaker.ShouldRecord = shouldRecord;
        return this;
    }
}

public static class ResiliencePolicyExtensions
{
    public static IResiliencePolicyProvider GetResiliencePolicyProvider(this object target)
    {
        return target is IHaveResiliencePolicyProvider accessor ? accessor.ResiliencePolicyProvider : null;
    }

    public static ValueTask ExecuteAsync(this IResiliencePolicy policy, Func<ValueTask> action, CancellationToken cancellationToken = default)
    {
        if (policy == null)
            throw new ArgumentNullException(nameof(policy));

        if (action == null)
            throw new ArgumentNullException(nameof(action));

        return policy.ExecuteAsync(_ => action(), cancellationToken);
    }

    public static ValueTask<T> ExecuteAsync<T>(this IResiliencePolicy policy, Func<ValueTask<T>> action, CancellationToken cancellationToken = default)
    {
        if (policy == null)
            throw new ArgumentNullException(nameof(policy));

        if (action == null)
            throw new ArgumentNullException(nameof(action));

        return policy.ExecuteAsync(_ => action(), cancellationToken);
    }
}
