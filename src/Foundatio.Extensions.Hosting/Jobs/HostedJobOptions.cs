using Foundatio.Jobs;

namespace Foundatio.Extensions.Hosting.Jobs;

public class HostedJobOptions : Foundatio.Jobs.Legacy.JobOptions
{
    public bool WaitForStartupActions { get; set; }
}
