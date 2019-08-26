using Foundatio.Jobs;

namespace Foundatio.Hosting.Jobs {
    public class HostedJobOptions : JobOptions {
        public bool WaitForStartupActions { get; set; }
        public string CronSchedule { get; set; }
    }
}
