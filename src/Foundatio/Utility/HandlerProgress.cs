using System;

namespace Foundatio.Utility;

/// <summary>Rules shared by every <see cref="IHandlerContext.ReportProgressAsync"/> implementation.</summary>
internal static class HandlerProgress
{
    /// <summary>How often a progress report reaches a store, a trace or a log. Reports in between only update the latest values.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);

    /// <exception cref="ArgumentOutOfRangeException"><paramref name="percent"/> is below 0 or above 100.</exception>
    public static void ThrowIfInvalid(int? percent)
    {
        if (percent is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(percent), percent, "Progress must be from 0 to 100 percent.");
    }
}
