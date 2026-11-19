using System;

namespace Foundatio.Lock;

internal readonly record struct LeaseTimestamp(long Timestamp, DateTimeOffset Utc)
{
    public static LeaseTimestamp Capture(TimeProvider clock) => new(clock.GetTimestamp(), clock.GetUtcNow());

    public bool HasElapsed(TimeSpan duration, TimeProvider clock) => ElapsedUntil(Capture(clock), clock) >= duration;

    public TimeSpan ElapsedUntil(LeaseTimestamp end, TimeProvider clock)
    {
        // Cache expiration may follow UTC. A backward clock adjustment must never extend a lease.
        var monotonic = clock.GetElapsedTime(Timestamp, end.Timestamp);
        var wallClock = end.Utc - Utc;
        return TimeSpan.FromTicks(Math.Max(monotonic.Ticks, wallClock.Ticks));
    }
}
