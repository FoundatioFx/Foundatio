using System;
using Foundatio.Resilience;
using Foundatio.Serializer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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

    /// <summary>
    /// How long shutdown waits for running jobs before cancelling them; cancelled jobs run again later without using
    /// an attempt. Jobs still running get 5 more seconds after cancellation. The wait starts when the host begins
    /// stopping and runs alongside the message handlers', so keep this plus 5 seconds under the host's
    /// <c>HostOptions.ShutdownTimeout</c> (30 seconds by default) and the orchestrator's grace period. Defaults to 20 seconds.
    /// </summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long succeeded jobs are kept, without their arguments. Zero stops reads returning a job as soon as it
    /// succeeds. Defaults to one day. A job type's <see cref="JobTypeOptions.RetainSucceededFor"/> and a CRON schedule's
    /// <see cref="CronJobOptions.RetainSucceededFor"/> override it.
    /// </summary>
    public TimeSpan RetainSucceededFor { get; set; } = TimeSpan.FromDays(1);

    /// <summary>How long failed and cancelled jobs are kept, with their arguments. Defaults to seven days.</summary>
    public TimeSpan RetainFailedFor { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Used when a job type does not set its own policy.</summary>
    public RetryPolicy DefaultRetryPolicy { get; set; } = RetryPolicy.Default;

    /// <summary>Defaults to the registered <see cref="ISerializer"/>, or JSON.</summary>
    public ISerializer? Serializer { get; set; }

    /// <summary>Identifies this worker in <see cref="JobDetails.WorkerId"/>. Defaults to the machine name and process id.</summary>
    public string? WorkerId { get; set; }

    /// <summary>
    /// The clock jobs run on: due times, leases, retry backoff, CRON occurrences and retention. Defaults to the
    /// registered <see cref="System.TimeProvider"/>, or the system clock. The store <c>UseInMemory()</c> creates uses it
    /// too. Set it to <see cref="System.TimeProvider.System"/> to keep jobs on real time while tests move the app's clock.
    /// </summary>
    public TimeProvider? TimeProvider { get; set; }

    /// <summary>Set by the test harness: replaces the delay of every retry policy, the job types' own included.</summary>
    internal TimeSpan? TestRetryDelay { get; set; }

    /// <summary>The clock jobs use in <paramref name="services"/>: <see cref="TimeProvider"/>, the registered one, or the system clock.</summary>
    internal static TimeProvider ResolveTimeProvider(IServiceProvider services)
    {
        return services.GetService<IOptions<JobWorkerOptions>>()?.Value.TimeProvider ?? services.GetService<TimeProvider>() ?? System.TimeProvider.System;
    }

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxConcurrency, 1, nameof(MaxConcurrency));
        ArgumentOutOfRangeException.ThrowIfLessThan(LeaseDuration, TimeSpan.FromSeconds(1), nameof(LeaseDuration));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(PollInterval, TimeSpan.Zero, nameof(PollInterval));
        ArgumentOutOfRangeException.ThrowIfLessThan(ShutdownTimeout, TimeSpan.Zero, nameof(ShutdownTimeout));
        ArgumentOutOfRangeException.ThrowIfLessThan(RetainSucceededFor, TimeSpan.Zero, nameof(RetainSucceededFor));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(RetainFailedFor, TimeSpan.Zero, nameof(RetainFailedFor));
        ArgumentNullException.ThrowIfNull(DefaultRetryPolicy, nameof(DefaultRetryPolicy));
        DefaultRetryPolicy.Validate();
    }
}
