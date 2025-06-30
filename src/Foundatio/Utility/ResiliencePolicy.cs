using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Utility.Resilience;

public interface IResiliencePolicyProvider
{
    IResiliencePolicy GetDefaultPolicy();
    IResiliencePolicy GetPolicy(string name, bool useDefault = true);
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
        _defaultPolicy = new ResiliencePolicy(_loggerFactory.CreateLogger<ResiliencePolicy>(), _timeProvider)
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

        var policy = new ResiliencePolicy(_loggerFactory.CreateLogger<ResiliencePolicy>(), _timeProvider);
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

        var policy = new ResiliencePolicy(_loggerFactory.CreateLogger<ResiliencePolicy>(), _timeProvider);
        var policyBuilder = new ResiliencePolicyBuilder(policy);
        builder(policyBuilder);

        _policies[name] = policy;
        return this;
    }

    public ResiliencePolicyProvider WithPolicy<T>(IResiliencePolicy policy)
    {
        string name = typeof(T).GetFriendlyTypeName();
        return WithPolicy(name, policy);
    }

    public ResiliencePolicyProvider WithPolicy<T>(Action<ResiliencePolicyBuilder> builder)
    {
        string name = typeof(T).GetFriendlyTypeName();
        return WithPolicy(name, builder);
    }

    public IResiliencePolicy GetDefaultPolicy() => _defaultPolicy;

    public IResiliencePolicy GetPolicy(string name, bool useDefault = true)
    {
        if (String.IsNullOrEmpty(name))
            throw new ArgumentNullException(nameof(name));

        return _policies.TryGetValue(name, out var policy) ? policy : useDefault ? _defaultPolicy : null;
    }
}

[DebuggerDisplay("MaxAttempts = {MaxAttempts} Delay={GetDelayType()}, Timeout = {GetTimeout()}, CircuitBreaker = {CircuitBreaker?.State}")]
public class ResiliencePolicy : IResiliencePolicy, IHaveTimeProvider, IHaveLogger
{
    private readonly TimeProvider _timeProvider;
    private ILogger _logger;

    public ResiliencePolicy(ILogger logger = null, TimeProvider timeProvider = null)
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

    private string GetDelayType()
    {
        if (Delay.HasValue)
            return "Fixed " + Delay.Value.TotalSeconds + "s";

        if (GetDelay != null && GetDelay.Method.Name.Contains("ExponentialDelay"))
            return "Exponential";

        if (GetDelay != null && GetDelay.Method.Name.Contains("LinearDelay"))
            return "Linear";

        if (GetDelay != null)
            return "Custom";

        return "No Delay";
    }

    private string GetTimeout()
    {
        if (Timeout > TimeSpan.Zero)
            return Timeout.TotalSeconds + "s";

        return "None";
    }

    private static readonly Random _random = new();

    public static Func<int, TimeSpan> ExponentialDelay(TimeSpan baseDelay, double exponentialFactor = 2.0)
    {
        return attempt => TimeSpan.FromMilliseconds(Math.Pow(exponentialFactor, attempt - 1) * baseDelay.TotalMilliseconds);
    }

    public static Func<int, TimeSpan> LinearDelay(TimeSpan baseDelay)
    {
        return attempt => TimeSpan.FromMilliseconds(attempt * baseDelay.TotalMilliseconds);
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
    private ILogger _logger;
    private readonly TimeProvider _timeProvider;

    private CircuitState _state = CircuitState.Closed;
    private DateTime? _periodStartTime;
    private int _periodCalls;
    private int _periodFailures;
    private DateTime? _breakStartTime;
    private TimeSpan _currentBreakDuration;

    public CircuitBreaker(ILogger logger = null, TimeProvider timeProvider = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
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
        return this;;
    }

    /// <summary>
    /// Sets the circuit breaker for this policy. If set to null, no circuit breaker is applied.
    /// </summary>
    /// <param name="circuitBreaker"></param>
    public ResiliencePolicyBuilder WithCircuitBreaker(ICircuitBreaker circuitBreaker)
    {
        _policy.CircuitBreaker = circuitBreaker;
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

public static class ResiliencePolicyExtensions
{
    /// <summary>
    /// Gets a resilience policy for the specified type from the provider, or creates a new default configuration one if not found.
    /// </summary>
    /// <param name="provider"></param>
    /// <param name="logger"></param>
    /// <param name="timeProvider"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static IResiliencePolicy GetPolicy<T>(this IResiliencePolicyProvider provider, ILogger logger = null, TimeProvider timeProvider = null)
    {
        IResiliencePolicy policy = provider?.GetPolicy(typeof(T).GetFriendlyTypeName(), false);
        return policy ?? GetDefaultPolicy(provider, null, logger, timeProvider);
    }

    /// <summary>
    /// Gets a resilience policy for the specified type from the provider, or creates a new one using the fallback builder if not found.
    /// </summary>
    /// <param name="provider"></param>
    /// <param name="fallbackBuilder"></param>
    /// <param name="logger"></param>
    /// <param name="timeProvider"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static IResiliencePolicy GetPolicy<T>(this IResiliencePolicyProvider provider, Action<ResiliencePolicyBuilder> fallbackBuilder, ILogger logger = null, TimeProvider timeProvider = null)
    {
        IResiliencePolicy policy = provider?.GetPolicy(typeof(T).GetFriendlyTypeName(), false);
        return policy ?? GetDefaultPolicy(provider, fallbackBuilder, logger, timeProvider);
    }

    /// <summary>
    /// Gets a resilience policy by checking the specified types in order from the provider, or creates a new default configuration one if not found.
    /// </summary>
    /// <param name="provider"></param>
    /// <param name="logger"></param>
    /// <param name="timeProvider"></param>
    /// <typeparam name="T1"></typeparam>
    /// <typeparam name="T2"></typeparam>
    /// <returns></returns>
    public static IResiliencePolicy GetPolicy<T1, T2>(this IResiliencePolicyProvider provider, ILogger logger = null, TimeProvider timeProvider = null)
    {
        if (provider != null)
        {
            IResiliencePolicy policy = provider.GetPolicy(typeof(T1).GetFriendlyTypeName(), false) ?? provider.GetPolicy(typeof(T2).GetFriendlyTypeName(), false);
            if (policy != null)
                return policy;
        }

        return GetDefaultPolicy(provider, null, logger, timeProvider);
    }

    /// <summary>
    /// Gets a resilience policy by checking the specified types in order from the provider, or creates a new one using the fallback builder if not found.
    /// </summary>
    /// <param name="provider"></param>
    /// <param name="fallbackBuilder"></param>
    /// <param name="logger"></param>
    /// <param name="timeProvider"></param>
    /// <typeparam name="T1"></typeparam>
    /// <typeparam name="T2"></typeparam>
    /// <returns></returns>
    public static IResiliencePolicy GetPolicy<T1, T2>(this IResiliencePolicyProvider provider, Action<ResiliencePolicyBuilder> fallbackBuilder, ILogger logger = null, TimeProvider timeProvider = null)
    {
        if (provider != null)
        {
            IResiliencePolicy policy = provider.GetPolicy(typeof(T1).GetFriendlyTypeName(), false) ?? provider.GetPolicy(typeof(T2).GetFriendlyTypeName(), false);
            if (policy != null)
                return policy;
        }

        return GetDefaultPolicy(provider, fallbackBuilder, logger, timeProvider);
    }

    /// <summary>
    /// Gets a resilience policy by checking the specified types in order from the provider, or creates a new default configuration one if not found.
    /// </summary>
    /// <param name="provider"></param>
    /// <param name="logger"></param>
    /// <param name="timeProvider"></param>
    /// <typeparam name="T1"></typeparam>
    /// <typeparam name="T2"></typeparam>
    /// <typeparam name="T3"></typeparam>
    /// <returns></returns>
    public static IResiliencePolicy GetPolicy<T1, T2, T3>(this IResiliencePolicyProvider provider, ILogger logger = null, TimeProvider timeProvider = null)
    {
        if (provider != null)
        {
            IResiliencePolicy policy = provider.GetPolicy(typeof(T1).GetFriendlyTypeName(), false) ?? provider.GetPolicy(typeof(T2).GetFriendlyTypeName(), false) ?? provider.GetPolicy(typeof(T3).GetFriendlyTypeName(), false);
            if (policy != null)
                return policy;
        }

        return GetDefaultPolicy(provider, null, logger, timeProvider);
    }

    /// <summary>
    /// Gets a resilience policy by checking the specified types in order from the provider, or creates a new one using the fallback builder if not found.
    /// </summary>
    /// <param name="provider"></param>
    /// <param name="fallbackBuilder"></param>
    /// <param name="logger"></param>
    /// <param name="timeProvider"></param>
    /// <typeparam name="T1"></typeparam>
    /// <typeparam name="T2"></typeparam>
    /// <typeparam name="T3"></typeparam>
    /// <returns></returns>
    public static IResiliencePolicy GetPolicy<T1, T2, T3>(this IResiliencePolicyProvider provider, Action<ResiliencePolicyBuilder> fallbackBuilder, ILogger logger = null, TimeProvider timeProvider = null)
    {
        IResiliencePolicy policy;

        if (provider != null)
        {
            policy = provider.GetPolicy(typeof(T1).GetFriendlyTypeName(), false) ?? provider.GetPolicy(typeof(T2).GetFriendlyTypeName(), false) ?? provider.GetPolicy(typeof(T3).GetFriendlyTypeName(), false);
            if (policy != null)
                return policy;
        }

        return GetDefaultPolicy(provider, fallbackBuilder, logger, timeProvider);
    }

    private static IResiliencePolicy GetDefaultPolicy(IResiliencePolicyProvider provider, Action<ResiliencePolicyBuilder> fallbackBuilder = null, ILogger logger = null, TimeProvider timeProvider = null)
    {
        if (provider != null && provider.GetType() != typeof(ResiliencePolicyProvider))
        {
            var defaultPolicy = provider.GetDefaultPolicy();
            if (defaultPolicy != null)
                return defaultPolicy;
        }

        var policy = new ResiliencePolicy(logger, timeProvider);
        fallbackBuilder?.Invoke(new ResiliencePolicyBuilder(policy));
        return policy;
    }

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
