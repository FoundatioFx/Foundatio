using System;
using Foundatio.Resilience;
using Foundatio.Serializer;

namespace Foundatio.Jobs;

/// <summary>Settings for the job worker and scheduler.</summary>
public sealed class JobWorkerOptions
{
    /// <summary>How many jobs this instance runs at once. Defaults to the processor count.</summary>
    public int MaxConcurrency { get; set; } = Environment.ProcessorCount;

    /// <summary>How long a running job is leased. Renewed automatically; another worker takes over if it expires.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>The longest an idle worker waits before checking for jobs enqueued by other processes.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How long shutdown waits for running jobs before cancelling them. Cancelled jobs run again later.</summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(25);

    /// <summary>How long succeeded jobs are kept, without their arguments. Defaults to one day.</summary>
    public TimeSpan RetainSucceededFor { get; set; } = TimeSpan.FromDays(1);

    /// <summary>How long failed and cancelled jobs are kept, with their arguments. Defaults to seven days.</summary>
    public TimeSpan RetainFailedFor { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Used when a job type does not set its own policy.</summary>
    public RetryPolicy DefaultRetryPolicy { get; set; } = RetryPolicy.Default;

    /// <summary>Defaults to the registered <see cref="ISerializer"/>, or JSON.</summary>
    public ISerializer? Serializer { get; set; }

    /// <summary>Identifies this worker in <see cref="JobDetails.WorkerId"/>. Defaults to the machine name and process id.</summary>
    public string? WorkerId { get; set; }

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxConcurrency, 1, nameof(MaxConcurrency));
        ArgumentOutOfRangeException.ThrowIfLessThan(LeaseDuration, TimeSpan.FromSeconds(1), nameof(LeaseDuration));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(PollInterval, TimeSpan.Zero, nameof(PollInterval));
        ArgumentOutOfRangeException.ThrowIfLessThan(ShutdownTimeout, TimeSpan.Zero, nameof(ShutdownTimeout));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(RetainSucceededFor, TimeSpan.Zero, nameof(RetainSucceededFor));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(RetainFailedFor, TimeSpan.Zero, nameof(RetainFailedFor));
        ArgumentNullException.ThrowIfNull(DefaultRetryPolicy, nameof(DefaultRetryPolicy));
        DefaultRetryPolicy.Validate();
    }
}
