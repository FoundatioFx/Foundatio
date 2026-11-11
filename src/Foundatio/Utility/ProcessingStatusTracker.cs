using System;
using System.Collections.Generic;

namespace Foundatio.Utility;

/// <summary>
/// The state of one background loop. Written by the loop and read by health checks, so updates are cheap when nothing
/// changes: <see cref="RecordSuccess"/> is called after every receive or claim.
/// </summary>
internal sealed class ProcessingStatusTracker
{
    private readonly TimeProvider _timeProvider;
    private readonly object _lock = new();
    private ProcessingState _state = ProcessingState.Starting;
    private DateTimeOffset? _failingSince;
    private DateTimeOffset? _lastErrorAt;
    private string? _lastError;
    private volatile bool _failing;

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

    public ProcessingStatus GetStatus(string name, int active = 0, IReadOnlyDictionary<string, int>? activeByName = null)
    {
        lock (_lock)
        {
            var status = new ProcessingStatus
            {
                Name = name,
                State = _state,
                Active = active,
                FailingSince = _failingSince,
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
