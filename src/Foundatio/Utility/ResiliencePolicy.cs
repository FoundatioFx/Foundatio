using System;
using System.Collections.Concurrent;
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

public class ResiliencePolicy : IResiliencePolicy
{
    private readonly TimeProvider _timeProvider;
    private ILogger _logger;

    public ResiliencePolicy(TimeProvider timeProvider = null, ILogger logger = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Gets or sets the logger for this policy.
    /// </summary>
    public ILogger Logger {
        get => _logger;
        set => _logger = value ?? NullLogger.Instance;
    }

    /// <summary>
    /// Sets a fixed retry interval for all retries.
    /// </summary>
    public TimeSpan? RetryInterval { get; set; }

    /// <summary>
    /// The maximum number of attempts to execute the action.
    /// </summary>
    public int? MaxAttempts { get; set; }

    /// <summary>
    /// A function that determines whether to retry based on the attempt number and exception.
    /// </summary>
    public Func<int, Exception, bool> ShouldRetry { get; set; }

    /// <summary>
    /// Gets or sets a function that returns the backoff interval based on the number of attempts.
    /// </summary>
    public Func<int, TimeSpan> GetBackoffInterval { get; set; } = attempts => TimeSpan.FromMilliseconds(_defaultBackoffIntervals[Math.Min(attempts, _defaultBackoffIntervals.Length - 1)]);

    /// <summary>
    /// Gets or sets a value indicating whether to use jitter in the backoff interval.
    /// </summary>
    public bool UseJitter { get; set; } = true;

    /// <summary>
    /// Gets or sets the timeout for the entire operation.
    /// </summary>
    public TimeSpan Timeout { get; set; }

    /// <summary>
    /// Gets or sets the circuit breaker for this policy.
    /// </summary>
    public IResiliencePolicyCircuitBreaker CircuitBreaker { get; set; }

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

        int maxAttempts = MaxAttempts ?? (ShouldRetry != null ? Int32.MaxValue : 5);
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
                CircuitBreaker?.BeforeAction();
                var result = await action(linkedCancellationToken).AnyContext();
                CircuitBreaker?.RecordSuccess();
                return result;
            }
            catch (Exception ex)
            {
                CircuitBreaker?.RecordFailure(ex);

                if ((ShouldRetry != null && !ShouldRetry(attempts, ex)) || attempts >= maxAttempts)
                    throw;

                _logger?.LogError(ex, "Retry error: {Message}", ex.Message);

                await _timeProvider.SafeDelay(GetInterval(attempts), linkedCancellationToken).AnyContext();

                ThrowIfTimedOut(startTime);
            }

            attempts++;
        } while (attempts <= maxAttempts && !linkedCancellationToken.IsCancellationRequested);

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

    private TimeSpan GetInterval(int attempts)
    {
        var interval = RetryInterval ?? GetBackoffInterval?.Invoke(attempts) ?? TimeSpan.FromMilliseconds(100);

        if (UseJitter)
        {
            double offset = interval.TotalMilliseconds * 0.5 / 2;
            double randomDelay = interval.TotalMilliseconds * 0.5 * _random.NextDouble() - offset;
            double newInterval = interval.TotalMilliseconds + randomDelay;
            interval = TimeSpan.FromMilliseconds(newInterval);
        }

        if (interval < TimeSpan.Zero)
            interval = TimeSpan.Zero;

        return interval;
    }

    private static readonly int[] _defaultBackoffIntervals = [ 100, 1000, 2000, 2000, 5000, 5000, 10000, 30000, 60000 ];
    private static readonly Random _random = new();
}

public interface IResiliencePolicyCircuitBreaker
{
    ResiliencePolicyCircuitState State { get; }
    void BeforeAction();
    void RecordSuccess();
    void RecordFailure(Exception ex);
}

public class ResiliencePolicyCircuitBreaker : IResiliencePolicyCircuitBreaker
{
    private readonly TimeProvider _timeProvider;

    private readonly TimeSpan _samplingDuration;
    private readonly double _failureRatio;
    private readonly int _minimumThroughput;
    private readonly TimeSpan _breakDuration;
    private readonly Func<Exception, bool> _shouldRecord;

    private ResiliencePolicyCircuitState _state = ResiliencePolicyCircuitState.Closed;
    private DateTime? _periodStartTime;
    private int _periodThroughput;
    private int _periodFailures;
    private DateTime? _breakStartTime;
    private readonly object _lock = new();

    public ResiliencePolicyCircuitBreaker(TimeProvider timeProvider = null, TimeSpan? samplingDuration = null, double failureRatio = 0.1, int minimumThroughput = 100, TimeSpan? breakDuration = null, Func<Exception, bool> shouldRecord = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _samplingDuration = samplingDuration ?? TimeSpan.FromSeconds(30);
        _failureRatio = failureRatio;
        _minimumThroughput = minimumThroughput;
        _breakDuration = breakDuration ?? TimeSpan.FromSeconds(5);
        _shouldRecord = shouldRecord;
    }

    public ResiliencePolicyCircuitState State => _state;

    public void Isolate()
    {
        _state = ResiliencePolicyCircuitState.Isolated;
    }

    public void Resume()
    {
        if (TryChangeState(ResiliencePolicyCircuitState.Isolated, ResiliencePolicyCircuitState.Closed)
            || TryChangeState(ResiliencePolicyCircuitState.Open, ResiliencePolicyCircuitState.Closed)
            || TryChangeState(ResiliencePolicyCircuitState.HalfOpen, ResiliencePolicyCircuitState.Closed))
        {
            ResetPeriod();
        }
    }

    public void BeforeAction()
    {
        bool shouldTest = false;
        if (_state == ResiliencePolicyCircuitState.Open && _breakStartTime != null)
        {
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            if (now.Subtract(_breakStartTime.Value) >= _breakDuration)
            {
                if (TryChangeState(ResiliencePolicyCircuitState.Open, ResiliencePolicyCircuitState.HalfOpen))
                {
                    _breakStartTime = now;
                    shouldTest = true;
                }
            }
        }

        switch (State)
        {
            case ResiliencePolicyCircuitState.Closed:
                break;
            case ResiliencePolicyCircuitState.HalfOpen:
                if (!shouldTest)
                    throw new BrokenCircuitException();
                break;
            case ResiliencePolicyCircuitState.Open:
            case ResiliencePolicyCircuitState.Isolated:
                throw new BrokenCircuitException();
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    private bool TryChangeState(ResiliencePolicyCircuitState oldState, ResiliencePolicyCircuitState newState)
    {
        var state = (ResiliencePolicyCircuitState)Interlocked.CompareExchange(
            ref Unsafe.As<ResiliencePolicyCircuitState, int>(ref _state),
            (int)newState,
            (int)oldState
        );

        return state == oldState;
    }

    public void RecordSuccess()
    {
        CheckPeriodStart();
        Interlocked.Increment(ref _periodThroughput);

        if (_state != ResiliencePolicyCircuitState.HalfOpen && _state != ResiliencePolicyCircuitState.Open)
            return;

        if (TryChangeState(ResiliencePolicyCircuitState.Open, ResiliencePolicyCircuitState.Closed)
            || TryChangeState(ResiliencePolicyCircuitState.HalfOpen, ResiliencePolicyCircuitState.Closed))
        {
            ResetPeriod();
        }
    }

    public void RecordFailure(Exception ex)
    {
        CheckPeriodStart();
        int count = Interlocked.Increment(ref _periodThroughput);

        if (_state == ResiliencePolicyCircuitState.HalfOpen && TryChangeState(ResiliencePolicyCircuitState.HalfOpen, ResiliencePolicyCircuitState.Open))
            _breakStartTime = _timeProvider.GetUtcNow().UtcDateTime;

        if (_shouldRecord != null && _shouldRecord.Invoke(ex) == false)
            return;

        int failureCount = Interlocked.Increment(ref _periodFailures);

        if (count < _minimumThroughput)
            return;

        // check failure rate
        if ((double)count / failureCount < _failureRatio)
            return;

        if (TryChangeState(ResiliencePolicyCircuitState.Closed, ResiliencePolicyCircuitState.Open))
            _breakStartTime = _timeProvider.GetUtcNow().UtcDateTime;
    }

    private void CheckPeriodStart()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        _periodStartTime ??= now;

        if (now.Subtract(_periodStartTime.Value) < _samplingDuration)
            return;

        ResetPeriod();
    }

    private void ResetPeriod()
    {
        _periodStartTime = null;
        Interlocked.Exchange(ref _periodThroughput, 0);
        Interlocked.Exchange(ref _periodFailures, 0);
    }
}

public enum ResiliencePolicyCircuitState
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
    /// Circuit is manually isolated and no attempts are allowed
    /// </summary>
    Isolated
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
    /// Sets a fixed retry interval for all retries.
    /// </summary>
    /// <param name="retryInterval"></param>
    public ResiliencePolicyBuilder WithRetryInterval(TimeSpan? retryInterval)
    {
        policy.RetryInterval = retryInterval;
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
    /// Sets a function that returns the backoff interval based on the number of attempts. This overrides the retry interval.
    /// </summary>
    /// <param name="getBackoffInterval"></param>
    public ResiliencePolicyBuilder WithBackoffInterval(Func<int, TimeSpan> getBackoffInterval)
    {
        policy.GetBackoffInterval = getBackoffInterval;
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
    ///
    /// </summary>
    /// <param name="circuitBreaker"></param>
    public ResiliencePolicyBuilder WithCircuitBreaker(IResiliencePolicyCircuitBreaker circuitBreaker)
    {
        policy.CircuitBreaker = circuitBreaker;
        return this;;
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
