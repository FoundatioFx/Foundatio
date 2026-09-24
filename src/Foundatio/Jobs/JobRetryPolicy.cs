using System;

namespace Foundatio.Jobs;

/// <summary>Serializable retry curve persisted with each job, independent of worker configuration.</summary>
public sealed record JobRetryPolicy
{
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromMinutes(5);
    public double Multiplier { get; init; } = 2;
    public double JitterFactor { get; init; } = 0.2;

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(InitialDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxDelay, InitialDelay);
        if (!Double.IsFinite(Multiplier) || Multiplier < 1) throw new ArgumentOutOfRangeException(nameof(Multiplier));
        if (!Double.IsFinite(JitterFactor) || JitterFactor is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(JitterFactor));
    }

    public TimeSpan GetDelay(int attempt)
    {
        Validate();
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        if (InitialDelay == TimeSpan.Zero)
            return TimeSpan.Zero;
        double seconds = Math.Min(MaxDelay.TotalSeconds, InitialDelay.TotalSeconds * Math.Pow(Multiplier, Math.Min(100, attempt - 1)));
        double jitter = 1 + JitterFactor * (2 * Random.Shared.NextDouble() - 1);
        return TimeSpan.FromSeconds(Math.Min(MaxDelay.TotalSeconds, seconds * jitter));
    }
}
