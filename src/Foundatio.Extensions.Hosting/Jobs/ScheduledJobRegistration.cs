using System;
using Foundatio.Jobs;

namespace Foundatio.Extensions.Hosting.Jobs;

public class ScheduledJobRegistration
{
    public ScheduledJobRegistration(string schedule, string jobName, Func<IServiceProvider, IJob> jobFactory)
    {
        Schedule = schedule;
        Name = jobName;
        JobFactory = jobFactory;
    }

    public string Schedule { get; }
    public string Name { get; }
    public Func<IServiceProvider, IJob> JobFactory { get; }
}
