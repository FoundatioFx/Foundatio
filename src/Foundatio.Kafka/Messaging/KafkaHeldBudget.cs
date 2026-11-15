using System;
using System.Diagnostics;

namespace Foundatio.Messaging.Transport;

/// <summary>
/// What a receiver does about the records held behind their groups, in one partition or in all, once they reach their
/// budget. It first stops reading, which keeps what librdkafka has fetched, so a backlog that drains quickly goes on
/// being read without being fetched again. Only if the held records do not drain to half the budget within the stop
/// timeout are their partitions paused, which drops what librdkafka has fetched for them but lets other partitions be
/// read; the next time they reach it they are paused at once, unless they drained within the timeout while paused.
/// Guarded by the receiver's lock.
/// </summary>
internal sealed class KafkaHeldBudget
{
    private long _stoppedAt;
    private long _pausedAt;
    private bool _slow;

    /// <summary>The held records reached the budget, so the receiver stops reading until they drain or the stop timeout passes.</summary>
    public bool Stopped => _stoppedAt != 0;

    /// <summary>The held records did not drain within the stop timeout, so their partitions are paused until they do.</summary>
    public bool Paused { get; private set; }

    /// <summary>
    /// Stops or pauses once the held records are <paramref name="over"/> the budget, and ends either once they are
    /// <paramref name="drained"/> to half of it. Returns whether <see cref="Stopped"/> or <see cref="Paused"/> changed.
    /// </summary>
    public bool Update(bool over, bool drained, TimeSpan stopTimeout)
    {
        if (Paused || Stopped)
        {
            if (!drained)
                return false;

            if (Paused)
                _slow = Stopwatch.GetElapsedTime(_pausedAt) > stopTimeout;

            Paused = false;
            _stoppedAt = 0;
            return true;
        }

        if (!over)
            return false;

        long now = Stopwatch.GetTimestamp();
        if (_slow)
            Pause(now);
        else
            _stoppedAt = now;

        return true;
    }

    /// <summary>
    /// Pauses once reading has been stopped for <paramref name="stopTimeout"/>. Returns how long until it would be,
    /// or <see cref="TimeSpan.Zero"/> once it is.
    /// </summary>
    public TimeSpan PauseIfStoppedFor(TimeSpan stopTimeout, long now)
    {
        var remaining = stopTimeout - Stopwatch.GetElapsedTime(_stoppedAt, now);
        if (remaining > TimeSpan.Zero)
            return remaining;

        _stoppedAt = 0;
        _slow = true;
        Pause(now);
        return TimeSpan.Zero;
    }

    /// <summary>
    /// Times the drain from now, for held records that could not be handed out until now (their partition's state was
    /// being read), so the wait does not count as a slow drain.
    /// </summary>
    public void Restart()
    {
        long now = Stopwatch.GetTimestamp();
        if (Stopped)
            _stoppedAt = now;

        if (Paused)
            _pausedAt = now;
    }

    private void Pause(long now)
    {
        Paused = true;
        _pausedAt = now;
    }
}
