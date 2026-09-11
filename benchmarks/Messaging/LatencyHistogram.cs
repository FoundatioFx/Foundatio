using System.Numerics;

namespace Foundatio.Messaging.Benchmarks;

public sealed class LatencyHistogram
{
    private readonly long[] _buckets = new long[2048];
    private long _count;
    private long _maximum;

    public void RecordMicroseconds(long microseconds)
    {
        microseconds = Math.Max(0, microseconds);
        int exponent = microseconds < 64 ? 0 : BitOperations.Log2((ulong)microseconds) - 6;
        int index = checked((int)(microseconds < 64 ? microseconds : (exponent * 64) + (microseconds >> exponent)));
        Interlocked.Increment(ref _buckets[Math.Min(index, _buckets.Length - 1)]);
        Interlocked.Increment(ref _count);
        long previous = Volatile.Read(ref _maximum);
        while (microseconds > previous)
        {
            long observed = Interlocked.CompareExchange(ref _maximum, microseconds, previous);
            if (observed == previous) break;
            previous = observed;
        }
    }

    public LatencySummary Snapshot()
    {
        long count = Volatile.Read(ref _count);
        double Percentile(double p)
        {
            if (count == 0) return 0;
            long target = (long)Math.Ceiling(count * p), accumulated = 0;
            for (int index = 0; index < _buckets.Length; index++)
            {
                accumulated += Volatile.Read(ref _buckets[index]);
                if (accumulated >= target)
                {
                    long upper = index < 64 ? index : ((65L + (index % 64)) << ((index / 64) - 1)) - 1;
                    return Math.Min(upper, Volatile.Read(ref _maximum)) / 1000d;
                }
            }
            return Volatile.Read(ref _maximum) / 1000d;
        }
        return new(count, Percentile(.5), Percentile(.95), Percentile(.99), Volatile.Read(ref _maximum) / 1000d);
    }
}

public sealed record LatencySummary(long Count, double P50Milliseconds, double P95Milliseconds, double P99Milliseconds, double MaxMilliseconds);
