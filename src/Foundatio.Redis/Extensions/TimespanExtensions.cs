using System;

namespace Foundatio.Extensions
{
    internal static class TimespanExtensions
    {
        public static TimeSpan Min(this TimeSpan source, TimeSpan other)
        {
            return source.Ticks > other.Ticks ? other : source;
        }
    }
}
