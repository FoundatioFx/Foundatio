using System;
using System.Collections.Generic;

namespace Foundatio.Utility;

/// <summary>
/// The state of one background loop. Written by the loop and read by health checks, so updates are cheap when nothing
/// changes: <see cref="RecordSuccess"/> is called after every receive or claim.
/// </summary>
internal sealed class ProcessingStatusTracker
{
    /// <summary>A settle failure stops counting as current when none has recurred for this long.</summary>
    private static readonly TimeSpan SettleFailureExpiry = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _timeProvider;
    private readonly object _lock = new();
    private ProcessingState _state = ProcessingState.Starting;
    private DateTimeOffset? _failingSince;
    private DateTimeOffset? _settleFailingSince;
    private DateTimeOffset _lastSettleFailureAt;
    private DateTimeOffset? _lastErrorAt;
    private string? _lastError;
    private volatile bool _failing;
    private volatile bool _settleFailing;

    public ProcessingStatusTracker(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    /// <summary>Connected and processing. Also ends a run of failures, since connecting succeeded.</summary>
    public void MarkRunning()
    {
        lock (_lock)
        {
            if (_state == ProcessingState.Starting)
                _state = ProcessingState.Running;

            ClearFailure();
        }
    }

    public void MarkStopped()
    {
        lock (_lock)
        {
            if (_state != ProcessingState.Faulted)
                _state = ProcessingState.Stopped;
        }
    }

    public void MarkFaulted(Exception exception)
    {
        lock (_lock)
        {
            _state = ProcessingState.Faulted;
            SetError(Describe(exception));
        }
    }

    public void RecordSuccess()
    {
        if (!_failing)
            return;

        lock (_lock)
            ClearFailure();
    }

    public void RecordFailure(Exception exception, string? context = null)
    {
        string error = context is null ? Describe(exception) : $"{context}: {Describe(exception)}";
        lock (_lock)
        {
            _failingSince ??= _timeProvider.GetUtcNow();
            _failing = true;
            SetError(error);
        }
    }

    /// <summary>
    /// Records a settlement that failed and will not resolve itself, such as a dead-letter the broker rejects. Unlike
    /// receive failures, a successful receive does not clear it; <see cref="RecordSettleSuccess"/> does, or ten minutes
    /// without another failure.
    /// </summary>
    public void RecordSettleFailure(Exception exception, string context)
    {
        lock (_lock)
        {
            var now = _timeProvider.GetUtcNow();
            if (_settleFailingSince is null || now - _lastSettleFailureAt >= SettleFailureExpiry)
                _settleFailingSince = now;

            _lastSettleFailureAt = now;
            _settleFailing = true;
            SetError($"{context}: {Describe(exception)}");
        }
    }

    public void RecordSettleSuccess()
    {
        if (!_settleFailing)
            return;

        lock (_lock)
        {
            _settleFailingSince = null;
            _settleFailing = false;
        }
    }

    public ProcessingStatus GetStatus(string name, int active = 0, IReadOnlyDictionary<string, int>? activeByName = null)
    {
        lock (_lock)
        {
            var settleFailingSince = _settleFailingSince is { } since && _timeProvider.GetUtcNow() - _lastSettleFailureAt < SettleFailureExpiry ? since : (DateTimeOffset?)null;
            var status = new ProcessingStatus
            {
                Name = name,
                State = _state,
                Active = active,
                FailingSince = _failingSince is { } receiveFailingSince && (settleFailingSince is null || receiveFailingSince < settleFailingSince) ? receiveFailingSince : settleFailingSince,
                LastError = _lastError,
                LastErrorAt = _lastErrorAt
            };

            return activeByName is null ? status : status with { ActiveByName = activeByName };
        }
    }

    private void SetError(string error)
    {
        _lastError = error;
        _lastErrorAt = _timeProvider.GetUtcNow();
    }

    private void ClearFailure()
    {
        _failingSince = null;
        _failing = false;
    }

    private static string Describe(Exception exception) => $"{exception.GetType().Name}: {exception.Message}";
}
