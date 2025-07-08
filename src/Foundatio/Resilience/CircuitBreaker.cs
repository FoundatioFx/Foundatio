using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Resilience;

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
    public ILogger Logger
    {
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
    public HashSet<Type> UnrecordedExceptions { get; set; } = [typeof(OperationCanceledException)];

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
