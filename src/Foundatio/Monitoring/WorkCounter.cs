using System;
using System.Threading;

namespace Foundatio.Monitoring;

/// <summary>
/// Rolling counts for one message type on one source, or one job name, in one-minute buckets. Updates are a few
/// interlocked operations; a bucket is replaced, not cleared, when its minute comes round again, so an update racing
/// the rotation can only be lost, never counted in the wrong minute.
/// </summary>
internal sealed class WorkCounter
{
    private static readonly int OutcomeCount = Enum.GetValues<WorkOutcome>().Length;

    private readonly Bucket?[] _buckets;
    private readonly long _timestampsPerMinute;
    private readonly int _windowMinutes;
    private int _inFlight;

    public WorkCounter(WorkKind kind, string name, string? source, int windowMinutes, long timestampFrequency)
    {
        Kind = kind;
        Name = name;
        Source = source;
        _windowMinutes = windowMinutes;
        _timestampsPerMinute = timestampFrequency * 60;

        // The window's buckets, the current one, and a spare, so rotating never replaces a bucket still in the window.
        _buckets = new Bucket?[windowMinutes + 2];
    }

    public WorkKind Kind { get; }
    public string Name { get; }
    public string? Source { get; }

    public int InFlight => Volatile.Read(ref _inFlight);

    public void Started() => Interlocked.Increment(ref _inFlight);

    public void Stopped() => Interlocked.Decrement(ref _inFlight);

    /// <summary>Counts one outcome at <paramref name="timestamp"/>, with how long it ran when it ran at all.</summary>
    public void Record(long timestamp, WorkOutcome outcome, TimeSpan? duration)
    {
        var bucket = GetBucket(timestamp / _timestampsPerMinute);
        Interlocked.Increment(ref bucket.Counts[(int)outcome]);
        if (duration is not { } value)
            return;

        Interlocked.Increment(ref bucket.Timed);
        Interlocked.Add(ref bucket.TotalTicks, value.Ticks);
        long max = Volatile.Read(ref bucket.MaxTicks);
        while (value.Ticks > max)
        {
            long previous = Interlocked.CompareExchange(ref bucket.MaxTicks, value.Ticks, max);
            if (previous == max)
                break;

            max = previous;
        }
    }

    /// <summary>Sums the buckets of the window ending at <paramref name="timestamp"/>; null when nothing happened in it and nothing runs.</summary>
    public WorkStatsEntry? GetStats(long timestamp)
    {
        long minute = timestamp / _timestampsPerMinute;
        var counts = new long[OutcomeCount];
        long timed = 0, totalTicks = 0, maxTicks = 0;
        for (int slot = 0; slot < _buckets.Length; slot++)
        {
            var bucket = Volatile.Read(ref _buckets[slot]);
            if (bucket is null || bucket.Minute < minute - _windowMinutes || bucket.Minute > minute)
                continue;

            for (int i = 0; i < counts.Length; i++)
                counts[i] += Volatile.Read(ref bucket.Counts[i]);

            timed += Volatile.Read(ref bucket.Timed);
            totalTicks += Volatile.Read(ref bucket.TotalTicks);
            maxTicks = Math.Max(maxTicks, Volatile.Read(ref bucket.MaxTicks));
        }

        int inFlight = InFlight;
        if (inFlight <= 0 && Array.TrueForAll(counts, c => c == 0))
            return null;

        return new WorkStatsEntry
        {
            Kind = Kind,
            Name = Name,
            Source = Source,
            Succeeded = counts[(int)WorkOutcome.Succeeded],
            Retried = counts[(int)WorkOutcome.Retried],
            Failed = counts[(int)WorkOutcome.Failed],
            DeadLettered = counts[(int)WorkOutcome.DeadLettered],
            Cancelled = counts[(int)WorkOutcome.Cancelled],
            Skipped = counts[(int)WorkOutcome.Skipped],
            InFlight = Math.Max(0, inFlight),
            Timed = timed,
            TotalDuration = TimeSpan.FromTicks(totalTicks),
            MaxDuration = TimeSpan.FromTicks(maxTicks)
        };
    }

    private Bucket GetBucket(long minute)
    {
        int slot = (int)(minute % _buckets.Length);
        var bucket = Volatile.Read(ref _buckets[slot]);
        if (bucket is not null && bucket.Minute >= minute)
            return bucket;

        var fresh = new Bucket(minute);
        var current = Interlocked.CompareExchange(ref _buckets[slot], fresh, bucket);
        return current == bucket ? fresh : current!;
    }

    private sealed class Bucket(long minute)
    {
        public readonly long Minute = minute;
        public readonly long[] Counts = new long[OutcomeCount];
        public long Timed;
        public long TotalTicks;
        public long MaxTicks;
    }
}
