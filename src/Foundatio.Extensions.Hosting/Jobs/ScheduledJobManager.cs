using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Foundatio.Extensions.Hosting.Jobs;

public interface IScheduledJobManager
{
    void AddOrUpdate<TJob>(string cronSchedule) where TJob : class, IJob;
    void AddOrUpdate(string jobName, string cronSchedule, Func<IServiceProvider, CancellationToken, Task> action);
    void AddOrUpdate(string jobName, string cronSchedule, Func<CancellationToken, Task> action);
    void AddOrUpdate(string jobName, string cronSchedule, Func<Task> action);
    void AddOrUpdate(string jobName, string cronSchedule, Action<IServiceProvider, CancellationToken> action);
    void AddOrUpdate(string jobName, string cronSchedule, Action<CancellationToken> action);
    void AddOrUpdate(string jobName, string cronSchedule, Action action);
    void Remove<TJob>() where TJob : class, IJob;
    void Remove(string jobName);
}

public class ScheduledJobManager : IScheduledJobManager
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ICacheClient _cacheClient;

    public ScheduledJobManager(IServiceProvider serviceProvider, ILoggerFactory loggerFactory)
    {
        _serviceProvider = serviceProvider;
        _loggerFactory = loggerFactory;
        _cacheClient = serviceProvider.GetService<ICacheClient>() ?? new InMemoryCacheClient(o => o.LoggerFactory(loggerFactory));
        Jobs.AddRange(serviceProvider.GetServices<ScheduledJobRegistration>().Select(j => new ScheduledJobRunner(j.Schedule, j.Name, j.JobFactory, serviceProvider, _cacheClient, loggerFactory)));
    }

    public void AddOrUpdate<TJob>(string cronSchedule) where TJob : class, IJob
    {
        string jobName = typeof(TJob).Name;
        var job = Jobs.FirstOrDefault(j => j.JobName == jobName);
        if (job == null)
        {
            Jobs.Add(new ScheduledJobRunner(cronSchedule, jobName, sp => sp.GetRequiredService<TJob>(), _serviceProvider, _cacheClient, _loggerFactory));
        }
        else
        {
            job.Schedule = cronSchedule;
        }
    }

    public void AddOrUpdate(string jobName, string cronSchedule, Func<IServiceProvider, CancellationToken, Task> action)
    {
        var job = Jobs.FirstOrDefault(j => j.JobName == jobName);
        if (job == null)
        {
            Jobs.Add(new ScheduledJobRunner(cronSchedule, jobName, sp => new DynamicJob(sp, action), _serviceProvider, _cacheClient, _loggerFactory));
        }
        else
        {
            job.Schedule = cronSchedule;
        }
    }

    public void AddOrUpdate(string jobName, string cronSchedule, Func<CancellationToken, Task> action)
    {
        AddOrUpdate(jobName, cronSchedule, (_, ct) => action(ct));
    }

    public void AddOrUpdate(string jobName, string cronSchedule, Func<Task> action)
    {
        AddOrUpdate(jobName, cronSchedule, (_, _) => action());
    }

    public void AddOrUpdate(string jobName, string cronSchedule, Action<IServiceProvider, CancellationToken> action)
    {
        AddOrUpdate(jobName, cronSchedule, (sp, ct) =>
        {
            action(sp, ct);
            return Task.CompletedTask;
        });
    }

    public void AddOrUpdate(string jobName, string cronSchedule, Action<CancellationToken> action)
    {
        AddOrUpdate(jobName, cronSchedule, (_, ct) =>
        {
            action(ct);
            return Task.CompletedTask;
        });
    }

    public void AddOrUpdate(string jobName, string cronSchedule, Action action)
    {
        AddOrUpdate(jobName, cronSchedule, (_, _) =>
        {
            action();
            return Task.CompletedTask;
        });
    }

    public void Remove<TJob>() where TJob : class, IJob
    {
        string jobName = typeof(TJob).Name;
        var job = Jobs.FirstOrDefault(j => j.JobName == jobName);
        if (job != null)
        {
            Jobs.Remove(job);
        }
    }

    public void Remove(string jobName)
    {
        var job = Jobs.FirstOrDefault(j => j.JobName == jobName);
        if (job != null)
        {
            Jobs.Remove(job);
        }
    }

    internal List<ScheduledJobRunner> Jobs { get; } = new();
}
