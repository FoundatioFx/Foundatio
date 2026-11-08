using System;
using System.Collections.Generic;

namespace Foundatio.Jobs;

/// <summary>Everything added by every <c>AddFoundatioJobs</c> call, read when the host starts.</summary>
internal sealed class JobsRegistrations
{
    public Dictionary<string, JobRegistration> Jobs { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, CronSchedule> Schedules { get; } = new(StringComparer.Ordinal);
    public List<Action<JobsOptions>> ConfigureOptions { get; } = [];
}
