using System;

namespace Foundatio.Lock;

internal readonly record struct LeaseTimestamp(long MonotonicTimestamp, DateTimeOffset CapturedAtUtc)
{
    public static LeaseTimestamp Capture(TimeProvider clock) => new(clock.GetTimestamp(), clock.GetUtcNow());

    public bool HasElapsed(TimeSpan duration, TimeProvider clock) => ElapsedUntil(Capture(clock), clock) >= duration;

    public TimeSpan ElapsedUntil(LeaseTimestamp end, TimeProvider clock)
    {
        // Cache expiration may follow UTC. A backward clock adjustment must never extend a lease.
        var monotonic = clock.GetElapsedTime(MonotonicTimestamp, end.MonotonicTimestamp);
        var wallClock = end.CapturedAtUtc - CapturedAtUtc;
        return TimeSpan.FromTicks(Math.Max(monotonic.Ticks, wallClock.Ticks));
    }
}
