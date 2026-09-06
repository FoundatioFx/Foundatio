using System.Diagnostics;

namespace Foundatio.Messaging.Benchmarks;

public static class RateSchedule
{
    public static async Task WaitUntilAsync(long timestamp, CancellationToken token)
    {
        while (Stopwatch.GetTimestamp() < timestamp)
        {
            var remaining = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), timestamp);
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, Math.Ceiling(remaining.TotalMilliseconds))), token);
        }
    }
}
