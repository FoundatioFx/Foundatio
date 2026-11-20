using System;
using Foundatio.Messaging.Transport;

namespace Foundatio.Messaging;

/// <summary>
/// Counts one receiver's failed receives and paces receives after a failure, when several receives may be in flight
/// at once. Receives that were in flight together when one failed fail for the same reason, so their failures count
/// once: the receiver is replaced after as many failed rounds as with one receive at a time, and every receive waits
/// out the same backoff.
/// </summary>
internal sealed class ReceiveBackoff
{
    /// <summary>How many failures in a row, without a successful receive since, replace the receiver.</summary>
    public const int ReplaceAfterFailures = 5;

    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _timeProvider;
    private readonly object _lock = new();
    private long _round;
    private int _failures;
    private TimeSpan _backoff;
    private DateTimeOffset _resumeAt;
    private bool _replacing;
    private volatile bool _failing;

    public ReceiveBackoff(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    /// <summary>The backoff after the last failure, which grows with each failure until a receive succeeds.</summary>
    public TimeSpan Current
    {
        get
        {
            lock (_lock)
                return _backoff;
        }
    }

    /// <summary>How long until the backoff after the last failure ends.</summary>
    public TimeSpan Remaining
    {
        get
        {
            lock (_lock)
                return RemainingLocked();
        }
    }

    /// <summary>
    /// Called before a receive: returns the round it belongs to, which identifies the failures it may still observe,
    /// and in <paramref name="wait"/> how long it must wait first for the backoff of an earlier failure.
    /// </summary>
    public long BeginReceive(out TimeSpan wait)
    {
        lock (_lock)
        {
            wait = RemainingLocked();
            return _round;
        }
    }

    /// <summary>A receive succeeded: once one that began after the last failure does, failures and backoff start over.</summary>
    public void RecordSuccess(long round)
    {
        if (!_failing)
            return;

        lock (_lock)
        {
            if (round != _round)
                return;

            _failures = 0;
            _backoff = TimeSpan.Zero;
            _failing = false;
        }
    }

    /// <summary>
    /// Records a failed receive that began in <paramref name="round"/>. Only the first failure of a round counts, grows
    /// the backoff and starts a new round. Returns true, once per receiver, when the receiver must be replaced: it
    /// faulted, or <see cref="ReplaceAfterFailures"/> failures counted in a row.
    /// </summary>
    public bool RecordFailure(Exception exception, long round, out bool counted, out TimeSpan backoff)
    {
        lock (_lock)
        {
            counted = round == _round;
            if (counted)
            {
                _round++;
                _failures++;
                _backoff = Next(_backoff);
                _resumeAt = _timeProvider.GetUtcNow() + _backoff;
                _failing = true;
            }

            backoff = _backoff;
            if (_replacing || !(exception is MessageReceiverFaultedException || counted && _failures >= ReplaceAfterFailures))
                return false;

            _replacing = true;
            return true;
        }
    }

    /// <summary>A new receiver replaced the failed one: failures count from zero, and the backoff stays until a receive succeeds.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _round++;
            _failures = 0;
            _resumeAt = default;
            _replacing = false;
        }
    }

    /// <summary>Doubles <paramref name="backoff"/>, from one second up to 30 seconds.</summary>
    public static TimeSpan Next(TimeSpan backoff)
        => backoff == TimeSpan.Zero ? TimeSpan.FromSeconds(1) : TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));

    private TimeSpan RemainingLocked()
    {
        var remaining = _resumeAt - _timeProvider.GetUtcNow();
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }
}
