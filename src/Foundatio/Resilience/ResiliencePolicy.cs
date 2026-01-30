using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Resilience;

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
    public ILogger Logger
    {
        get => _logger;
        set => _logger = value ?? NullLogger.Instance;
    }

    /// <summary>
    /// The maximum number of attempts to execute the action. Default is 3 attempts.
    /// </summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>
    /// A collection of exception types that will not be handled by the policy. These exceptions will be thrown immediately without retrying. Default includes OperationCanceledException and BrokenCircuitException.
    /// </summary>
    public HashSet<Type> UnhandledExceptions { get; set; } = [typeof(OperationCanceledException), typeof(BrokenCircuitException)];

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

    // ============================================================
    // SYNCHRONOUS METHODS
    // ============================================================

    public void Execute(Action<CancellationToken> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        int attempts = 1;
        var startTime = _timeProvider.GetUtcNow();
        var linkedCancellationToken = cancellationToken;
        var timeoutToken = CancellationToken.None;
        CancellationTokenSource timeoutCts = null;
        CancellationTokenSource linkedCts = null;

        if (Timeout > TimeSpan.Zero)
        {
            timeoutCts = new CancellationTokenSource(Timeout);
            timeoutToken = timeoutCts.Token;
            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutToken);
            linkedCancellationToken = linkedCts.Token;
        }

        try
        {
            do
            {
                try
                {
                    if (attempts > 1)
                        _logger?.LogInformation("Retrying {Attempts} attempt after {Duration:g}...", attempts.ToOrdinal(), _timeProvider.GetUtcNow().Subtract(startTime));

                    CircuitBreaker?.BeforeCall();
                    action(linkedCancellationToken);
                    CircuitBreaker?.RecordCallSuccess();
                    return;
                }
                catch (BrokenCircuitException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (ex is TaskCanceledException && timeoutToken.IsCancellationRequested)
                        throw new TimeoutException($"Operation timed out after {Timeout:g}.");

                    CircuitBreaker?.RecordCallFailure(ex);

                    if (attempts >= MaxAttempts || (ShouldRetry != null && !ShouldRetry(attempts, ex)) || UnhandledExceptions.Contains(ex.GetType()))
                        throw;

                    _logger?.LogError(ex, "Retry error: {Message}", ex.Message);

                    Thread.Sleep(GetAttemptDelay(attempts));

                    ThrowIfTimedOut(startTime);
                }

                attempts++;
            } while (attempts <= MaxAttempts && !linkedCancellationToken.IsCancellationRequested);

            throw new OperationCanceledException("Operation was canceled", linkedCancellationToken);
        }
        finally
        {
            linkedCts?.Dispose();
            timeoutCts?.Dispose();
        }
    }

    public TResult Execute<TResult>(Func<CancellationToken, TResult> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        int attempts = 1;
        var startTime = _timeProvider.GetUtcNow();
        var linkedCancellationToken = cancellationToken;
        var timeoutToken = CancellationToken.None;
        CancellationTokenSource timeoutCts = null;
        CancellationTokenSource linkedCts = null;

        if (Timeout > TimeSpan.Zero)
        {
            timeoutCts = new CancellationTokenSource(Timeout);
            timeoutToken = timeoutCts.Token;
            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutToken);
            linkedCancellationToken = linkedCts.Token;
        }

        try
        {
            do
            {
                try
                {
                    if (attempts > 1)
                        _logger?.LogInformation("Retrying {Attempts} attempt after {Duration:g}...", attempts.ToOrdinal(), _timeProvider.GetUtcNow().Subtract(startTime));

                    CircuitBreaker?.BeforeCall();
                    var result = action(linkedCancellationToken);
                    CircuitBreaker?.RecordCallSuccess();
                    return result;
                }
                catch (BrokenCircuitException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (ex is TaskCanceledException && timeoutToken.IsCancellationRequested)
                        throw new TimeoutException($"Operation timed out after {Timeout:g}.");

                    CircuitBreaker?.RecordCallFailure(ex);

                    if (attempts >= MaxAttempts || (ShouldRetry != null && !ShouldRetry(attempts, ex)) || UnhandledExceptions.Contains(ex.GetType()))
                        throw;

                    _logger?.LogError(ex, "Retry error: {Message}", ex.Message);

                    Thread.Sleep(GetAttemptDelay(attempts));

                    ThrowIfTimedOut(startTime);
                }

                attempts++;
            } while (attempts <= MaxAttempts && !linkedCancellationToken.IsCancellationRequested);

            throw new OperationCanceledException("Operation was canceled", linkedCancellationToken);
        }
        finally
        {
            linkedCts?.Dispose();
            timeoutCts?.Dispose();
        }
    }

    public void Execute<TState>(TState state, Action<TState, CancellationToken> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        int attempts = 1;
        var startTime = _timeProvider.GetUtcNow();
        var linkedCancellationToken = cancellationToken;
        var timeoutToken = CancellationToken.None;
        CancellationTokenSource timeoutCts = null;
        CancellationTokenSource linkedCts = null;

        if (Timeout > TimeSpan.Zero)
        {
            timeoutCts = new CancellationTokenSource(Timeout);
            timeoutToken = timeoutCts.Token;
            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutToken);
            linkedCancellationToken = linkedCts.Token;
        }

        try
        {
            do
            {
                try
                {
                    if (attempts > 1)
                        _logger?.LogInformation("Retrying {Attempts} attempt after {Duration:g}...", attempts.ToOrdinal(), _timeProvider.GetUtcNow().Subtract(startTime));

                    CircuitBreaker?.BeforeCall();
                    action(state, linkedCancellationToken);
                    CircuitBreaker?.RecordCallSuccess();
                    return;
                }
                catch (BrokenCircuitException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (ex is TaskCanceledException && timeoutToken.IsCancellationRequested)
                        throw new TimeoutException($"Operation timed out after {Timeout:g}.");

                    CircuitBreaker?.RecordCallFailure(ex);

                    if (attempts >= MaxAttempts || (ShouldRetry != null && !ShouldRetry(attempts, ex)) || UnhandledExceptions.Contains(ex.GetType()))
                        throw;

                    _logger?.LogError(ex, "Retry error: {Message}", ex.Message);

                    Thread.Sleep(GetAttemptDelay(attempts));

                    ThrowIfTimedOut(startTime);
                }

                attempts++;
            } while (attempts <= MaxAttempts && !linkedCancellationToken.IsCancellationRequested);

            throw new OperationCanceledException("Operation was canceled", linkedCancellationToken);
        }
        finally
        {
            linkedCts?.Dispose();
            timeoutCts?.Dispose();
        }
    }

    public TResult Execute<TState, TResult>(TState state, Func<TState, CancellationToken, TResult> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        int attempts = 1;
        var startTime = _timeProvider.GetUtcNow();
        var linkedCancellationToken = cancellationToken;
        var timeoutToken = CancellationToken.None;
        CancellationTokenSource timeoutCts = null;
        CancellationTokenSource linkedCts = null;

        if (Timeout > TimeSpan.Zero)
        {
            timeoutCts = new CancellationTokenSource(Timeout);
            timeoutToken = timeoutCts.Token;
            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutToken);
            linkedCancellationToken = linkedCts.Token;
        }

        try
        {
            do
            {
                try
                {
                    if (attempts > 1)
                        _logger?.LogInformation("Retrying {Attempts} attempt after {Duration:g}...", attempts.ToOrdinal(), _timeProvider.GetUtcNow().Subtract(startTime));

                    CircuitBreaker?.BeforeCall();
                    var result = action(state, linkedCancellationToken);
                    CircuitBreaker?.RecordCallSuccess();
                    return result;
                }
                catch (BrokenCircuitException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (ex is TaskCanceledException && timeoutToken.IsCancellationRequested)
                        throw new TimeoutException($"Operation timed out after {Timeout:g}.");

                    CircuitBreaker?.RecordCallFailure(ex);

                    if (attempts >= MaxAttempts || (ShouldRetry != null && !ShouldRetry(attempts, ex)) || UnhandledExceptions.Contains(ex.GetType()))
                        throw;

                    _logger?.LogError(ex, "Retry error: {Message}", ex.Message);

                    Thread.Sleep(GetAttemptDelay(attempts));

                    ThrowIfTimedOut(startTime);
                }

                attempts++;
            } while (attempts <= MaxAttempts && !linkedCancellationToken.IsCancellationRequested);

            throw new OperationCanceledException("Operation was canceled", linkedCancellationToken);
        }
        finally
        {
            linkedCts?.Dispose();
            timeoutCts?.Dispose();
        }
    }

    // ============================================================
    // ASYNCHRONOUS METHODS
    // ============================================================

    public async ValueTask ExecuteAsync(Func<CancellationToken, ValueTask> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        int attempts = 1;
        var startTime = _timeProvider.GetUtcNow();
        var linkedCancellationToken = cancellationToken;
        var timeoutToken = CancellationToken.None;
        CancellationTokenSource timeoutCts = null;
        CancellationTokenSource linkedCts = null;

        if (Timeout > TimeSpan.Zero)
        {
            timeoutCts = new CancellationTokenSource(Timeout);
            timeoutToken = timeoutCts.Token;
            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutToken);
            linkedCancellationToken = linkedCts.Token;
        }

        try
        {
            do
            {
                try
                {
                    if (attempts > 1)
                        _logger?.LogInformation("Retrying {Attempts} attempt after {Duration:g}...", attempts.ToOrdinal(), _timeProvider.GetUtcNow().Subtract(startTime));

                    CircuitBreaker?.BeforeCall();
                    await action(linkedCancellationToken).AnyContext();
                    CircuitBreaker?.RecordCallSuccess();
                    return;
                }
                catch (BrokenCircuitException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (ex is TaskCanceledException && timeoutToken.IsCancellationRequested)
                        throw new TimeoutException($"Operation timed out after {Timeout:g}.");

                    CircuitBreaker?.RecordCallFailure(ex);

                    if (attempts >= MaxAttempts || (ShouldRetry != null && !ShouldRetry(attempts, ex)) || UnhandledExceptions.Contains(ex.GetType()))
                        throw;

                    _logger?.LogError(ex, "Retry error: {Message}", ex.Message);

                    await _timeProvider.SafeDelay(GetAttemptDelay(attempts), linkedCancellationToken).AnyContext();

                    ThrowIfTimedOut(startTime);
                }

                attempts++;
            } while (attempts <= MaxAttempts && !linkedCancellationToken.IsCancellationRequested);

            throw new OperationCanceledException("Operation was canceled", linkedCancellationToken);
        }
        finally
        {
            linkedCts?.Dispose();
            timeoutCts?.Dispose();
        }
    }

    public async ValueTask<T> ExecuteAsync<T>(Func<CancellationToken, ValueTask<T>> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        int attempts = 1;
        var startTime = _timeProvider.GetUtcNow();
        var linkedCancellationToken = cancellationToken;
        var timeoutToken = CancellationToken.None;
        CancellationTokenSource timeoutCts = null;
        CancellationTokenSource linkedCts = null;

        if (Timeout > TimeSpan.Zero)
        {
            timeoutCts = new CancellationTokenSource(Timeout);
            timeoutToken = timeoutCts.Token;
            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutToken);
            linkedCancellationToken = linkedCts.Token;
        }

        try
        {
            do
            {
                try
                {
                    if (attempts > 1)
                        _logger?.LogInformation("Retrying {Attempts} attempt after {Duration:g}...", attempts.ToOrdinal(), _timeProvider.GetUtcNow().Subtract(startTime));

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
                    if (ex is TaskCanceledException && timeoutToken.IsCancellationRequested)
                        throw new TimeoutException($"Operation timed out after {Timeout:g}.");

                    CircuitBreaker?.RecordCallFailure(ex);

                    if (attempts >= MaxAttempts || (ShouldRetry != null && !ShouldRetry(attempts, ex)) || UnhandledExceptions.Contains(ex.GetType()))
                        throw;

                    _logger?.LogError(ex, "Retry error: {Message}", ex.Message);

                    await _timeProvider.SafeDelay(GetAttemptDelay(attempts), linkedCancellationToken).AnyContext();

                    ThrowIfTimedOut(startTime);
                }

                attempts++;
            } while (attempts <= MaxAttempts && !linkedCancellationToken.IsCancellationRequested);

            throw new OperationCanceledException("Operation was canceled", linkedCancellationToken);
        }
        finally
        {
            linkedCts?.Dispose();
            timeoutCts?.Dispose();
        }
    }

    public async ValueTask ExecuteAsync<TState>(TState state, Func<TState, CancellationToken, ValueTask> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        int attempts = 1;
        var startTime = _timeProvider.GetUtcNow();
        var linkedCancellationToken = cancellationToken;
        var timeoutToken = CancellationToken.None;
        CancellationTokenSource timeoutCts = null;
        CancellationTokenSource linkedCts = null;

        if (Timeout > TimeSpan.Zero)
        {
            timeoutCts = new CancellationTokenSource(Timeout);
            timeoutToken = timeoutCts.Token;
            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutToken);
            linkedCancellationToken = linkedCts.Token;
        }

        try
        {
            do
            {
                try
                {
                    if (attempts > 1)
                        _logger?.LogInformation("Retrying {Attempts} attempt after {Duration:g}...", attempts.ToOrdinal(), _timeProvider.GetUtcNow().Subtract(startTime));

                    CircuitBreaker?.BeforeCall();
                    await action(state, linkedCancellationToken).AnyContext();
                    CircuitBreaker?.RecordCallSuccess();
                    return;
                }
                catch (BrokenCircuitException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (ex is TaskCanceledException && timeoutToken.IsCancellationRequested)
                        throw new TimeoutException($"Operation timed out after {Timeout:g}.");

                    CircuitBreaker?.RecordCallFailure(ex);

                    if (attempts >= MaxAttempts || (ShouldRetry != null && !ShouldRetry(attempts, ex)) || UnhandledExceptions.Contains(ex.GetType()))
                        throw;

                    _logger?.LogError(ex, "Retry error: {Message}", ex.Message);

                    await _timeProvider.SafeDelay(GetAttemptDelay(attempts), linkedCancellationToken).AnyContext();

                    ThrowIfTimedOut(startTime);
                }

                attempts++;
            } while (attempts <= MaxAttempts && !linkedCancellationToken.IsCancellationRequested);

            throw new OperationCanceledException("Operation was canceled", linkedCancellationToken);
        }
        finally
        {
            linkedCts?.Dispose();
            timeoutCts?.Dispose();
        }
    }

    public async ValueTask<TResult> ExecuteAsync<TState, TResult>(TState state, Func<TState, CancellationToken, ValueTask<TResult>> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        int attempts = 1;
        var startTime = _timeProvider.GetUtcNow();
        var linkedCancellationToken = cancellationToken;
        var timeoutToken = CancellationToken.None;
        CancellationTokenSource timeoutCts = null;
        CancellationTokenSource linkedCts = null;

        if (Timeout > TimeSpan.Zero)
        {
            timeoutCts = new CancellationTokenSource(Timeout);
            timeoutToken = timeoutCts.Token;
            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutToken);
            linkedCancellationToken = linkedCts.Token;
        }

        try
        {
            do
            {
                try
                {
                    if (attempts > 1)
                        _logger?.LogInformation("Retrying {Attempts} attempt after {Duration:g}...", attempts.ToOrdinal(), _timeProvider.GetUtcNow().Subtract(startTime));

                    CircuitBreaker?.BeforeCall();
                    var result = await action(state, linkedCancellationToken).AnyContext();
                    CircuitBreaker?.RecordCallSuccess();
                    return result;
                }
                catch (BrokenCircuitException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (ex is TaskCanceledException && timeoutToken.IsCancellationRequested)
                        throw new TimeoutException($"Operation timed out after {Timeout:g}.");

                    CircuitBreaker?.RecordCallFailure(ex);

                    if (attempts >= MaxAttempts || (ShouldRetry != null && !ShouldRetry(attempts, ex)) || UnhandledExceptions.Contains(ex.GetType()))
                        throw;

                    _logger?.LogError(ex, "Retry error: {Message}", ex.Message);

                    await _timeProvider.SafeDelay(GetAttemptDelay(attempts), linkedCancellationToken).AnyContext();

                    ThrowIfTimedOut(startTime);
                }

                attempts++;
            } while (attempts <= MaxAttempts && !linkedCancellationToken.IsCancellationRequested);

            throw new OperationCanceledException("Operation was canceled", linkedCancellationToken);
        }
        finally
        {
            linkedCts?.Dispose();
            timeoutCts?.Dispose();
        }
    }

    public ResiliencePolicy Clone(int? maxAttempts = null, TimeSpan? timeout = null, TimeSpan? delay = null, Func<int, TimeSpan> getDelay = null)
    {
        var clone = new ResiliencePolicy(_logger, _timeProvider)
        {
            MaxAttempts = maxAttempts ?? MaxAttempts,
            Timeout = timeout ?? Timeout,
            Delay = delay ?? Delay,
            GetDelay = getDelay ?? GetDelay,
            UseJitter = UseJitter,
            MaxDelay = MaxDelay,
            CircuitBreaker = CircuitBreaker
        };

        foreach (var exceptionType in UnhandledExceptions)
            clone.UnhandledExceptions.Add(exceptionType);

        clone.ShouldRetry = ShouldRetry;

        return clone;
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
#if NET6_0_OR_GREATER
            double randomDelay = delay.TotalMilliseconds * 0.5 * Random.Shared.NextDouble() - offset;
#else
            double randomDelay = delay.TotalMilliseconds * 0.5 * _random.Value.NextDouble() - offset;
#endif
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

#if !NET6_0_OR_GREATER
    private static readonly ThreadLocal<Random> _random = new(() => new Random());
#endif

    public static Func<int, TimeSpan> ExponentialDelay(TimeSpan baseDelay, double exponentialFactor = 2.0)
    {
        return attempt => TimeSpan.FromMilliseconds(Math.Pow(exponentialFactor, attempt - 1) * baseDelay.TotalMilliseconds);
    }

    public static Func<int, TimeSpan> LinearDelay(TimeSpan baseDelay)
    {
        return attempt => TimeSpan.FromMilliseconds(attempt * baseDelay.TotalMilliseconds);
    }
}
