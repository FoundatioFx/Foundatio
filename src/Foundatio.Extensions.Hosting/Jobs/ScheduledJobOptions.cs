using System;
using Foundatio.Jobs;

namespace Foundatio.Extensions.Hosting.Jobs;

public class ScheduledJobOptions
{
    public string Name { get; set; }
    public string Description { get; set; }
    public Func<IServiceProvider, IJob> JobFactory { get; set; }
    public bool WaitForStartupActions { get; set; }
    public string CronSchedule { get; set; }
    public TimeZoneInfo CronTimeZone { get; set; }
    public bool IsDistributed { get; set; }
}
