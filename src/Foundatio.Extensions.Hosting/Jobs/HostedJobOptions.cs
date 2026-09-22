using System;
using Foundatio.Jobs;

namespace Foundatio.Extensions.Hosting.Jobs;

public class HostedJobOptions : JobOptions
{
    public bool WaitForStartupActions { get; set; }

    /// <summary>
    /// Gets or sets how long to wait for startup actions. Null uses the five-minute default.
    /// Nonpositive durations time out immediately; infinite waits are not supported.
    /// Applies only when <see cref="WaitForStartupActions"/> is enabled.
    /// </summary>
    public TimeSpan? StartupActionsTimeout { get; set; }
}
