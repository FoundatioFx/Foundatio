using System;
using System.Threading;
using System.Threading.Tasks;
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

    public ScheduledJobOptionsBuilder CronTimeZone(string id)
    {
        Target.CronTimeZone = TimeZoneInfo.FindSystemTimeZoneById(id);
        return this;
    }

    public ScheduledJobOptionsBuilder CronTimeZone(TimeZoneInfo value)
    {
        Target.CronTimeZone = value;
        return this;
    }

    public ScheduledJobOptionsBuilder JobFactory(Func<IServiceProvider, IJob> value)
    {
        Target.JobFactory = value;
        return this;
    }

    public ScheduledJobOptionsBuilder JobAction(Func<IServiceProvider, CancellationToken, Task> action)
    {
        Target.JobFactory = sp => new DynamicJob(sp, action);
        return this;
    }

    public ScheduledJobOptionsBuilder JobAction(Func<IServiceProvider, Task> action)
    {
        Target.JobFactory = sp => new DynamicJob(sp, (xp, _) => action(xp));
        return this;
    }

    public ScheduledJobOptionsBuilder JobAction(Func<Task> action)
    {
        Target.JobFactory = sp => new DynamicJob(sp, (_, _) => action());
        return this;
    }

    public ScheduledJobOptionsBuilder JobAction(Action<CancellationToken> action)
    {
        Target.JobFactory = sp => new DynamicJob(sp, (_, ct) =>
        {
            action(ct);
            return Task.CompletedTask;
        });
        return this;
    }

    public ScheduledJobOptionsBuilder JobAction(Action action)
    {
        Target.JobFactory = sp => new DynamicJob(sp, (_, _) =>
        {
            action();
            return Task.CompletedTask;
        });
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

    public ScheduledJobOptionsBuilder Enabled(bool value = true)
    {
        Target.IsEnabled = value;
        return this;
    }

    public ScheduledJobOptionsBuilder Disabled()
    {
        Target.IsEnabled = false;
        return this;
    }
}
