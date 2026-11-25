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

    /// <summary>
    /// How long jobs of this type are kept after they succeed, without their arguments. Zero stops reads returning a
    /// job as soon as it succeeds. Defaults to <see cref="JobWorkerOptions.RetainSucceededFor"/>; a CRON schedule's
    /// <see cref="CronJobOptions.RetainSucceededFor"/> overrides it for the jobs it creates.
    /// </summary>
    public TimeSpan? RetainSucceededFor { get; init; }

    internal void Validate()
    {
        RetryPolicy?.Validate();
        if (MaxConcurrency is { } concurrency)
            ArgumentOutOfRangeException.ThrowIfLessThan(concurrency, 1, nameof(MaxConcurrency));
        if (RetainSucceededFor is { } retention)
            ArgumentOutOfRangeException.ThrowIfLessThan(retention, TimeSpan.Zero, nameof(RetainSucceededFor));
    }
}
