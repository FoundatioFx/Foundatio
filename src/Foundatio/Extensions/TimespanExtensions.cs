using System;
using System.Threading;

namespace Foundatio.Utility
{
    internal static class TimeSpanExtensions
    {
        public static CancellationTokenSource ToCancellationTokenSource(this TimeSpan timeout)
        {
            if (timeout == TimeSpan.Zero)
            {
                var source = new CancellationTokenSource();
                source.Cancel();
                return source;
            }

            if (timeout.Ticks > 0)
                return new CancellationTokenSource(timeout);

            return new CancellationTokenSource();
        }

        public static CancellationTokenSource ToCancellationTokenSource(this TimeSpan? timeout)
        {
            if (timeout.HasValue)
                return timeout.Value.ToCancellationTokenSource();

            return new CancellationTokenSource();
        }

        public static CancellationTokenSource ToCancellationTokenSource(this TimeSpan? timeout, TimeSpan defaultTimeout)
        {
            return (timeout ?? defaultTimeout).ToCancellationTokenSource();
        }

        public static TimeSpan Min(this TimeSpan source, TimeSpan other)
        {
            return source.Ticks > other.Ticks ? other : source;
        }

        public static TimeSpan Max(this TimeSpan source, TimeSpan other)
        {
            return source.Ticks < other.Ticks ? other : source;
        }
    }
}
