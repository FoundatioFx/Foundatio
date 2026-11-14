using System;
using System.Collections.Generic;

namespace Foundatio.Messaging.Transport;

/// <summary>
/// Fixed delays, one topic each. A record waits in its tier topic until its append time plus the tier's delay, so
/// due times never decrease along a partition and the forwarder only ever waits on a partition's head. A longer
/// delay hops through tiers; what remains below the smallest tier is held in memory by the forwarder.
/// </summary>
internal static class KafkaDelayTiers
{
    public static IReadOnlyList<TimeSpan> All { get; } =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(6)
    ];

    public static TimeSpan Smallest => All[0];

    /// <summary>The longest tier that does not overshoot <paramref name="remaining"/>, or the smallest tier.</summary>
    public static TimeSpan Choose(TimeSpan remaining)
    {
        for (int i = All.Count - 1; i > 0; i--)
        {
            if (All[i] <= remaining)
                return All[i];
        }

        return All[0];
    }

    public static string Label(TimeSpan tier) => tier.TotalHours >= 1 ? $"{tier.TotalHours:0}h"
        : tier.TotalMinutes >= 1 ? $"{tier.TotalMinutes:0}m"
        : $"{tier.TotalSeconds:0}s";
}
