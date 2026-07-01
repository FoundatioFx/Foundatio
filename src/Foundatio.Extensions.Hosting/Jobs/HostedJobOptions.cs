using Foundatio.Jobs;

namespace Foundatio.Extensions.Hosting.Jobs.Legacy;

public class HostedJobOptions : Foundatio.Jobs.Legacy.JobOptions
{
    public bool WaitForStartupActions { get; set; }
}
