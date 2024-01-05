using Foundatio.Jobs;

namespace Foundatio.Extensions.Hosting.Jobs;

public class HostedJobOptions : JobOptions
{
    public bool WaitForStartupActions { get; set; }
    public string CronSchedule { get; set; }
}
