using System;
using Foundatio.Jobs;

namespace Foundatio.Extensions.Hosting.Jobs;

public class ScheduledJobOptionsBuilder
{
    public ScheduledJobOptionsBuilder(ScheduledJobOptions target = null)
    {
        Target = target ?? new ScheduledJobOptions();
    }

    public ScheduledJobOptions Target { get; }

    public ScheduledJobOptionsBuilder Name(string value)
    {
        Target.Name = value;
        return this;
    }

    public ScheduledJobOptionsBuilder Description(string value)
    {
        Target.Description = value;
        return this;
    }

    public ScheduledJobOptionsBuilder CronSchedule(string value)
    {
        Target.CronSchedule = value;
        return this;
    }

    public ScheduledJobOptionsBuilder JobFactory(Func<IServiceProvider, IJob> value)
    {
        Target.JobFactory = value;
        return this;
    }

    public ScheduledJobOptionsBuilder WaitForStartupActions(bool value = true)
    {
        Target.WaitForStartupActions = value;
        return this;
    }

    public ScheduledJobOptionsBuilder Distributed(bool value = true)
    {
        Target.IsDistributed = value;
        return this;
    }
}
