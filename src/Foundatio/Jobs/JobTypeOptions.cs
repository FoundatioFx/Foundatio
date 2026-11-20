using System;
using Foundatio.Resilience;

namespace Foundatio.Jobs;

/// <summary>Settings for one job type.</summary>
public sealed record JobTypeOptions
{
    /// <summary>Defaults to <see cref="JobWorkerOptions.DefaultRetryPolicy"/>.</summary>
    public RetryPolicy? RetryPolicy { get; init; }

    /// <summary>How many jobs of this type one instance runs at once. Limited by <see cref="JobWorkerOptions.MaxConcurrency"/>.</summary>
    public int? MaxConcurrency { get; init; }

    internal void Validate()
    {
        RetryPolicy?.Validate();
        if (MaxConcurrency is { } concurrency)
            ArgumentOutOfRangeException.ThrowIfLessThan(concurrency, 1, nameof(MaxConcurrency));
    }
}
